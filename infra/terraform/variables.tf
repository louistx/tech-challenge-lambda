variable "region" {
  type    = string
  default = "us-east-1"
}
variable "environment" {
  type = string
  validation {
    condition     = contains(["homolog", "production"], var.environment)
    error_message = "Use homolog or production."
  }
}
variable "artifact_path" {
  type    = string
  default = "../../artifacts/lambda.zip"
}
variable "secret_arn" {
  type        = string
  description = "Existing Secrets Manager ARN. Values are never managed by Terraform."
}
variable "kms_key_arn" {
  type        = string
  default     = null
  description = "Optional CMK used by the secret; grants decrypt only via Secrets Manager."
}
variable "subnet_ids" {
  type = list(string)
  validation {
    condition     = length(var.subnet_ids) >= 2
    error_message = "Provide private subnets in at least two availability zones."
  }
}
variable "security_group_ids" {
  type        = list(string)
  description = "Existing SGs with PostgreSQL and HTTPS egress, no inbound required."
}
variable "allowed_origins" {
  type = list(string)
  validation {
    condition     = length(var.allowed_origins) > 0 && alltrue([for o in var.allowed_origins : startswith(o, "https://") && !strcontains(o, "*")])
    error_message = "Explicit HTTPS origins are required; wildcards are forbidden."
  }
}
variable "email_sender" { type = string }
variable "ses_identity_arn" { type = string }
variable "jwt_key_id" { type = string }
variable "jwt_audience" {
  type    = string
  default = "tech-challenge-api"
}
variable "previous_public_jwks" {
  type    = string
  default = "{\"keys\":[]}"
}
variable "alarm_topic_arn" {
  type        = string
  default     = null
  description = "Existing SNS alarm destination; null creates alarms without notifications."
}
variable "reserved_concurrency" {
  type    = number
  default = 5
}
