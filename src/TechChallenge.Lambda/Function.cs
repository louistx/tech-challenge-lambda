using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace TechChallenge.Lambda;

public sealed partial class Function
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Initialization = new(1, 1);
    private static AuthService? productionService;
    private readonly Func<Task<AuthService>> getService;
    private readonly Action<string> log;

    public Function() : this(GetServiceAsync, Console.WriteLine) { }
    public Function(Func<Task<AuthService>> getService, Action<string> log)
    { this.getService = getService; this.log = log; }

    private static async Task<AuthService> GetServiceAsync()
    {
        if (productionService is not null) return productionService;
        await Initialization.WaitAsync();
        try { return productionService ??= await CreateServiceAsync(); }
        finally { Initialization.Release(); }
    }

    private static async Task<AuthService> CreateServiceAsync()
    {
        var settings = await Settings.LoadAsync();
        return new AuthService(settings, new ClientRepository(settings.DatabaseConnection), new Notifications(settings), new JwtTokens(settings));
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9-]{1,64}\z")]
    private static partial Regex CorrelationPattern();
    [GeneratedRegex(@"\A[0-9]{6}\z")]
    private static partial Regex OtpPattern();

    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(JsonElement input, ILambdaContext? context)
    {
        var started = Stopwatch.StartNew();
        var correlation = Guid.NewGuid().ToString();
        var requestId = context?.AwsRequestId ?? Guid.NewGuid().ToString();
        var operation = "unknown";
        var status = 200;
        string? error = null;
        object body = new { };
        var deliveryFailed = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            if (input.ValueKind != JsonValueKind.Object) throw new AuthException("invalid_payload", 400);
            if (input.TryGetProperty("source", out var source) && source.GetString() == "aws.events"
                && !input.TryGetProperty("requestContext", out _))
            {
                operation = "scheduled_cleanup";
                await (await getService()).CleanupAsync(deadline.Token);
                body = new { status = "cleaned" };
            }
            else
            {
                var request = input.Deserialize<APIGatewayHttpApiV2ProxyRequest>(Json)
                    ?? throw new AuthException("invalid_payload", 400);
                var headers = new Dictionary<string, string>(request.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                if (headers.TryGetValue("x-correlation-id", out var supplied) && CorrelationPattern().IsMatch(supplied)) correlation = supplied;
                var route = $"{request.RequestContext?.Http?.Method} {request.RawPath}";
                string[] routes = ["POST /auth/login", "POST /auth/verify", "GET /auth/me",
                    "GET /.well-known/jwks.json", "GET /.well-known/openid-configuration"];
                if (!routes.Contains(route)) throw new AuthException("not_found", 404);
                operation = route;
                var auth = await getService();
                var ip = request.RequestContext?.Http?.SourceIp;
                if (route.StartsWith("POST", StringComparison.Ordinal) && string.IsNullOrEmpty(ip))
                    throw new AuthException("invalid_request_context", 400);
                switch (route)
                {
                    case "POST /auth/login":
                        using (var payload = Payload(request, headers, ["cpf"]))
                        {
                            var result = await auth.LoginAsync(ReadString(payload, "cpf"), ip!, deadline.Token);
                            deliveryFailed = result.DeliveryFailed;
                            body = new
                            {
                                challenge_id = result.ChallengeId,
                                expires_in = 300,
                                message = "Se o cadastro estiver apto, enviaremos um código ao contato cadastrado."
                            };
                            status = 202;
                        }
                        break;
                    case "POST /auth/verify":
                        using (var payload = Payload(request, headers, ["challenge_id", "code"]))
                        {
                            var id = ReadString(payload, "challenge_id");
                            var code = ReadString(payload, "code");
                            if (!Guid.TryParseExact(id, "D", out _) || code is null || !OtpPattern().IsMatch(code))
                                throw new AuthException("invalid_payload", 400);
                            body = new
                            {
                                access_token = await auth.VerifyAsync(id!, code, ip!, deadline.Token),
                                token_type = "Bearer",
                                expires_in = auth.Settings.TokenLifetimeSeconds
                            };
                        }
                        break;
                    case "GET /.well-known/jwks.json": body = auth.Tokens.Jwks(); break;
                    case "GET /.well-known/openid-configuration":
                        body = new
                        {
                            issuer = auth.Settings.Issuer,
                            jwks_uri = auth.Settings.Issuer.TrimEnd('/') + "/.well-known/jwks.json",
                            id_token_signing_alg_values_supported = new[] { "RS256" },
                            response_types_supported = new[] { "token" },
                            subject_types_supported = new[] { "public" }
                        };
                        break;
                    case "GET /auth/me":
                        if (!headers.TryGetValue("authorization", out var authorization) || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
                            throw new AuthException("unauthorized", 401);
                        var claims = auth.Tokens.Verify(authorization[7..]);
                        body = new { sub = claims.FindFirst("sub")!.Value, scope = "customer" };
                        break;
                }
            }
        }
        catch (AuthException exception) { status = exception.Status; error = exception.Code; }
        catch (SecurityTokenException) { status = 401; error = "unauthorized"; }
        catch (JsonException) { status = 400; error = "invalid_payload"; }
        catch (NpgsqlException) { status = 503; error = "database_unavailable"; }
        catch (OperationCanceledException) { status = 503; error = "request_timeout"; }
        catch (Exception) { status = 503; error = "service_unavailable"; }
        if (error is not null) body = new { error = new { code = error }, correlation_id = correlation };
        var metrics = new Dictionary<string, object>
        {
            ["Requests"] = 1,
            ["Failures"] = status >= 400 ? 1 : 0,
            ["Latency"] = started.Elapsed.TotalMilliseconds,
            ["AuthSuccess"] = operation == "POST /auth/verify" && status == 200 ? 1 : 0,
            ["DatabaseErrors"] = error == "database_unavailable" ? 1 : 0,
            ["NotificationErrors"] = deliveryFailed ? 1 : 0,
            ["Timeouts"] = error == "request_timeout" ? 1 : 0
        };
        var record = new Dictionary<string, object?>(metrics.ToDictionary(p => p.Key, p => (object?)p.Value))
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["level"] = status >= 500 || deliveryFailed ? "ERROR" : "INFO",
            ["service"] = "tech-challenge-auth",
            ["environment"] = Environment.GetEnvironmentVariable("APP_ENV") ?? "local",
            ["request_id"] = requestId,
            ["correlation_id"] = correlation,
            ["operation"] = operation,
            ["status"] = status,
            ["error_code"] = error,
            ["_aws"] = new
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CloudWatchMetrics = new[] { new {
                Namespace = "TechChallenge/Auth", Dimensions = new[] { new[] { "service", "environment" } },
                Metrics = metrics.Keys.Select(name => new { Name = name, Unit = name == "Latency" ? "Milliseconds" : "Count" }) } }
            }
        };
        log(JsonSerializer.Serialize(record));
        var responseHeaders = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["cache-control"] = "no-store",
            ["x-correlation-id"] = correlation,
            ["x-content-type-options"] = "nosniff"
        };
        if (status == 429) responseHeaders["retry-after"] = "300";
        if (status == 401) responseHeaders["www-authenticate"] = "Bearer";
        return new APIGatewayHttpApiV2ProxyResponse
        { StatusCode = status, Headers = responseHeaders, Body = JsonSerializer.Serialize(body, Json) };
    }

    private static string? ReadString(JsonDocument document, string name)
    {
        if (!document.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new AuthException("invalid_payload", 400);
        return value.GetString();
    }

    private static JsonDocument Payload(APIGatewayHttpApiV2ProxyRequest request, Dictionary<string, string> headers, string[] fields)
    {
        if (!headers.TryGetValue("content-type", out var contentType)
            || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            throw new AuthException("unsupported_media_type", 415);
        var raw = request.Body ?? "";
        if (raw.Length > 8192) throw new AuthException("payload_too_large", 413);
        byte[] bytes;
        try { bytes = request.IsBase64Encoded ? Convert.FromBase64String(raw) : Encoding.UTF8.GetBytes(raw); }
        catch (FormatException) { throw new AuthException("invalid_payload", 400); }
        if (bytes.Length > 4096) throw new AuthException("payload_too_large", 413);
        var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Any(p => !fields.Contains(p.Name))
            || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct().Count() != document.RootElement.EnumerateObject().Count())
        {
            document.Dispose();
            throw new AuthException("invalid_payload", 400);
        }
        return document;
    }
}
