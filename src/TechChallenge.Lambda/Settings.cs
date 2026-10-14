using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Npgsql;

namespace TechChallenge.Lambda;

public sealed class Settings
{
    public required string DatabaseConnection { get; init; }
    public required string PrivateKey { get; init; }
    public required string OtpSecret { get; init; }
    public string Environment { get; init; } = "local";
    public string Issuer { get; init; } = "http://localhost:8080";
    public string Audience { get; init; } = "tech-challenge-api";
    public string KeyId { get; init; } = "local-1";
    public int TokenLifetimeSeconds { get; init; } = 900;
    public string EmailSender { get; init; } = "auth@local.test";
    public string SmtpHost { get; init; } = "localhost";
    public int SmtpPort { get; init; } = 1025;
    public string PreviousPublicJwks { get; init; } = "{\"keys\":[]}";
    public bool IsCloud => Environment is "homolog" or "production";

    public void Validate()
    {
        if (Environment is not ("local" or "test" or "homolog" or "production"))
            throw new InvalidOperationException("Invalid environment");
        if (OtpSecret.Length < 32 || TokenLifetimeSeconds is < 60 or > 900)
            throw new InvalidOperationException("Invalid security settings");
        if (string.IsNullOrWhiteSpace(Audience) || string.IsNullOrWhiteSpace(KeyId))
            throw new InvalidOperationException("Missing JWT settings");
        if (IsCloud)
        {
            var connection = new NpgsqlConnectionStringBuilder(DatabaseConnection);
            if (connection.SslMode != SslMode.VerifyFull || !Issuer.StartsWith("https://", StringComparison.Ordinal)
                || KeyId == "local-1")
                throw new InvalidOperationException("Cloud requires verified TLS and explicit signing key id");
        }
    }

    public static async Task<Settings> LoadAsync(CancellationToken cancellation = default)
    {
        static string Env(string key, string fallback = "") => System.Environment.GetEnvironmentVariable(key) ?? fallback;
        var environment = Env("APP_ENV", "local");
        string database, privateKey, otpSecret;
        if (environment is "homolog" or "production")
        {
            using var client = new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig
            { Timeout = TimeSpan.FromSeconds(3), MaxErrorRetry = 1 });
            var secret = await client.GetSecretValueAsync(new GetSecretValueRequest
            { SecretId = Env("AUTH_SECRET_ARN") }, cancellation);
            using var json = JsonDocument.Parse(secret.SecretString);
            database = json.RootElement.GetProperty("database_connection").GetString()!;
            privateKey = json.RootElement.GetProperty("private_key").GetString()!;
            otpSecret = json.RootElement.GetProperty("otp_secret").GetString()!;
        }
        else
        {
            database = Env("DATABASE_CONNECTION");
            privateKey = await File.ReadAllTextAsync(Env("JWT_PRIVATE_KEY_FILE", ".local/private.pem"), cancellation);
            otpSecret = Env("OTP_HMAC_SECRET");
        }
        var result = new Settings
        {
            Environment = environment,
            DatabaseConnection = database,
            PrivateKey = privateKey,
            OtpSecret = otpSecret,
            Issuer = Env("JWT_ISSUER", "http://localhost:8080"),
            Audience = Env("JWT_AUDIENCE", "tech-challenge-api"),
            KeyId = Env("JWT_KEY_ID", "local-1"),
            EmailSender = Env("EMAIL_SENDER", "auth@local.test"),
            SmtpHost = Env("SMTP_HOST", "localhost"),
            SmtpPort = int.Parse(Env("SMTP_PORT", "1025")),
            TokenLifetimeSeconds = int.Parse(Env("TOKEN_TTL", "900")),
            PreviousPublicJwks = Env("PREVIOUS_PUBLIC_JWKS", "{\"keys\":[]}")
        };
        result.Validate();
        return result;
    }
}
