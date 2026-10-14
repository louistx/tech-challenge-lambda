locals {
  name = "tech-challenge-auth-${var.environment}"
}
data "aws_caller_identity" "current" {}
data "aws_partition" "current" {}

resource "aws_cloudwatch_log_group" "lambda" {
  name              = "/aws/lambda/${local.name}"
  retention_in_days = 30
}
resource "aws_cloudwatch_log_group" "gateway" {
  name              = "/aws/apigateway/${local.name}"
  retention_in_days = 30
}
resource "aws_iam_role" "lambda" {
  name = local.name
  assume_role_policy = jsonencode({ Version = "2012-10-17", Statement = [{
    Effect = "Allow", Principal = { Service = "lambda.amazonaws.com" }, Action = "sts:AssumeRole"
  }] })
}
resource "aws_iam_role_policy" "lambda" {
  role = aws_iam_role.lambda.id
  policy = jsonencode({ Version = "2012-10-17", Statement = concat([
    { Effect = "Allow", Action = ["logs:CreateLogStream", "logs:PutLogEvents"], Resource = "${aws_cloudwatch_log_group.lambda.arn}:*" },
    { Effect = "Allow", Action = ["secretsmanager:GetSecretValue"], Resource = var.secret_arn },
    { Effect = "Allow", Action = ["ses:SendEmail"], Resource = var.ses_identity_arn,
    Condition = { StringEquals = { "ses:FromAddress" = var.email_sender } } },
    # Lambda VPC ENI lifecycle APIs require wildcard resources.
    { Effect = "Allow", Action = ["ec2:CreateNetworkInterface", "ec2:DescribeNetworkInterfaces", "ec2:DescribeSubnets", "ec2:DeleteNetworkInterface", "ec2:AssignPrivateIpAddresses", "ec2:UnassignPrivateIpAddresses"], Resource = "*" },
    # Deny EC2 use from function code while permitting Lambda's service ENI operations.
    { Effect = "Deny", Action = ["ec2:CreateNetworkInterface", "ec2:DeleteNetworkInterface", "ec2:DescribeSubnets", "ec2:AssignPrivateIpAddresses", "ec2:UnassignPrivateIpAddresses"], Resource = "*",
    Condition = { ArnEquals = { "lambda:SourceFunctionArn" = "arn:${data.aws_partition.current.partition}:lambda:${var.region}:${data.aws_caller_identity.current.account_id}:function:${local.name}" } } }
    ], var.kms_key_arn == null ? [] : [
    { Effect = "Allow", Action = ["kms:Decrypt"], Resource = var.kms_key_arn,
    Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${var.region}.amazonaws.com" } } }
  ]) })
}
resource "aws_apigatewayv2_api" "auth" {
  name          = local.name
  protocol_type = "HTTP"
  cors_configuration {
    allow_origins  = var.allowed_origins
    allow_methods  = ["GET", "POST", "OPTIONS"]
    allow_headers  = ["authorization", "content-type", "x-correlation-id"]
    expose_headers = ["x-correlation-id"]
    max_age        = 300
  }
}
resource "aws_lambda_function" "auth" {
  function_name                  = local.name
  role                           = aws_iam_role.lambda.arn
  runtime                        = "dotnet10"
  handler                        = "TechChallenge.Lambda::TechChallenge.Lambda.Function::FunctionHandler"
  architectures                  = ["x86_64"]
  filename                       = var.artifact_path
  source_code_hash               = filebase64sha256(var.artifact_path)
  memory_size                    = 256
  timeout                        = 15
  reserved_concurrent_executions = var.reserved_concurrency
  publish                        = true
  vpc_config {
    subnet_ids         = var.subnet_ids
    security_group_ids = var.security_group_ids
  }
  environment {
    variables = {
      APP_ENV              = var.environment
      AUTH_SECRET_ARN      = var.secret_arn
      JWT_ISSUER           = aws_apigatewayv2_api.auth.api_endpoint
      JWT_AUDIENCE         = var.jwt_audience
      JWT_KEY_ID           = var.jwt_key_id
      PREVIOUS_PUBLIC_JWKS = var.previous_public_jwks
      NOTIFIER             = "ses"
      EMAIL_SENDER         = var.email_sender
    }
  }
  depends_on = [aws_iam_role_policy.lambda, aws_cloudwatch_log_group.lambda]
}
resource "aws_lambda_alias" "live" {
  name             = "live"
  function_name    = aws_lambda_function.auth.function_name
  function_version = aws_lambda_function.auth.version
}
resource "aws_apigatewayv2_integration" "lambda" {
  api_id                 = aws_apigatewayv2_api.auth.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_alias.live.invoke_arn
  payload_format_version = "2.0"
  timeout_milliseconds   = 15000
}
resource "aws_apigatewayv2_authorizer" "jwt" {
  api_id           = aws_apigatewayv2_api.auth.id
  name             = "customer-jwt"
  authorizer_type  = "JWT"
  identity_sources = ["$request.header.Authorization"]
  jwt_configuration {
    issuer   = aws_apigatewayv2_api.auth.api_endpoint
    audience = [var.jwt_audience]
  }
}
resource "aws_apigatewayv2_route" "public" {
  for_each  = toset(["POST /auth/login", "POST /auth/verify", "GET /.well-known/jwks.json", "GET /.well-known/openid-configuration"])
  api_id    = aws_apigatewayv2_api.auth.id
  route_key = each.value
  target    = "integrations/${aws_apigatewayv2_integration.lambda.id}"
}
resource "aws_apigatewayv2_route" "me" {
  api_id               = aws_apigatewayv2_api.auth.id
  route_key            = "GET /auth/me"
  target               = "integrations/${aws_apigatewayv2_integration.lambda.id}"
  authorization_type   = "JWT"
  authorizer_id        = aws_apigatewayv2_authorizer.jwt.id
  authorization_scopes = ["customer"]
}
resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.auth.id
  name        = "$default"
  auto_deploy = true
  default_route_settings {
    throttling_burst_limit = 20
    throttling_rate_limit  = 10
  }
  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.gateway.arn
    format          = jsonencode({ requestId = "$context.requestId", status = "$context.status", routeKey = "$context.routeKey", responseLatency = "$context.responseLatency" })
  }
}
resource "aws_lambda_permission" "gateway" {
  statement_id  = "AllowHttpApi"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.auth.function_name
  qualifier     = aws_lambda_alias.live.name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.auth.execution_arn}/*/*"
}
resource "aws_cloudwatch_metric_alarm" "errors" {
  alarm_name          = "${local.name}-errors"
  namespace           = "AWS/Lambda"
  metric_name         = "Errors"
  dimensions          = { FunctionName = aws_lambda_function.auth.function_name }
  comparison_operator = "GreaterThanOrEqualToThreshold"
  threshold           = 3
  evaluation_periods  = 1
  period              = 300
  statistic           = "Sum"
  treat_missing_data  = "notBreaching"
  alarm_actions       = var.alarm_topic_arn == null ? [] : [var.alarm_topic_arn]
}
resource "aws_cloudwatch_metric_alarm" "database" {
  alarm_name          = "${local.name}-database"
  namespace           = "TechChallenge/Auth"
  metric_name         = "DatabaseErrors"
  dimensions          = { service = "tech-challenge-auth", environment = var.environment }
  comparison_operator = "GreaterThanOrEqualToThreshold"
  threshold           = 3
  evaluation_periods  = 1
  period              = 300
  statistic           = "Sum"
  treat_missing_data  = "notBreaching"
  alarm_actions       = var.alarm_topic_arn == null ? [] : [var.alarm_topic_arn]
}
resource "aws_cloudwatch_dashboard" "auth" {
  dashboard_name = local.name
  dashboard_body = jsonencode({ widgets = [{ type = "metric", x = 0, y = 0, width = 24, height = 6, properties = {
    region = var.region, title = "Authentication requests and failures", view = "timeSeries", period = 300, stat = "Sum",
    metrics = [["TechChallenge/Auth", "Requests", "service", "tech-challenge-auth", "environment", var.environment],
    [".", "Failures", ".", ".", ".", "."], [".", "AuthSuccess", ".", ".", ".", "."]]
    } }, { type = "metric", x = 0, y = 6, width = 24, height = 6, properties = {
    region  = var.region, title = "Auth latency p95", view = "timeSeries", period = 300, stat = "p95",
    metrics = [["TechChallenge/Auth", "Latency", "service", "tech-challenge-auth", "environment", var.environment]]
  } }] })
}

resource "aws_cloudwatch_event_rule" "cleanup" {
  name                = "${local.name}-cleanup"
  schedule_expression = "rate(1 hour)"
}
resource "aws_cloudwatch_event_target" "cleanup" {
  rule = aws_cloudwatch_event_rule.cleanup.name
  arn  = aws_lambda_alias.live.arn
  retry_policy {
    maximum_event_age_in_seconds = 3600
    maximum_retry_attempts       = 2
  }
}
resource "aws_lambda_permission" "cleanup" {
  statement_id  = "AllowScheduledCleanup"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.auth.function_name
  qualifier     = aws_lambda_alias.live.name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.cleanup.arn
}

resource "aws_cloudwatch_metric_alarm" "integration" {
  for_each            = toset(["NotificationErrors", "Timeouts"])
  alarm_name          = "${local.name}-${lower(each.value)}"
  namespace           = "TechChallenge/Auth"
  metric_name         = each.value
  dimensions          = { service = "tech-challenge-auth", environment = var.environment }
  comparison_operator = "GreaterThanOrEqualToThreshold"
  threshold           = 3
  evaluation_periods  = 1
  period              = 300
  statistic           = "Sum"
  treat_missing_data  = "notBreaching"
  alarm_actions       = var.alarm_topic_arn == null ? [] : [var.alarm_topic_arn]
}
