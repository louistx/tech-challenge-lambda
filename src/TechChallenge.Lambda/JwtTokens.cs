using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace TechChallenge.Lambda;

public sealed class JwtTokens : IDisposable
{
    private readonly Settings settings;
    private readonly RSA rsa;
    private readonly RsaSecurityKey signingKey;
    private readonly Dictionary<string, SecurityKey> verificationKeys = new(StringComparer.Ordinal);
    private readonly List<object> publicKeys = [];
    private readonly JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };

    public JwtTokens(Settings settings)
    {
        this.settings = settings;
        rsa = RSA.Create();
        rsa.ImportFromPem(settings.PrivateKey);
        if (rsa.KeySize < 2048) throw new InvalidOperationException("RSA key must be at least 2048 bits");
        signingKey = new RsaSecurityKey(rsa) { KeyId = settings.KeyId };
        var parameters = rsa.ExportParameters(false);
        verificationKeys.Add(settings.KeyId, new RsaSecurityKey(parameters) { KeyId = settings.KeyId });
        publicKeys.Add(new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = settings.KeyId,
            n = Base64UrlEncoder.Encode(parameters.Modulus!),
            e = Base64UrlEncoder.Encode(parameters.Exponent!)
        });
        using var previous = JsonDocument.Parse(settings.PreviousPublicJwks);
        foreach (var element in previous.RootElement.GetProperty("keys").EnumerateArray())
        {
            string[] allowed = ["kty", "use", "alg", "kid", "n", "e"];
            if (element.EnumerateObject().Any(p => !allowed.Contains(p.Name)))
                throw new InvalidOperationException("Only public JWK fields may be published");
            var key = new JsonWebKey(element.GetRawText());
            if (key.Kty != "RSA" || key.Alg != "RS256" || string.IsNullOrEmpty(key.Kid)
                || Base64UrlEncoder.DecodeBytes(key.N).Length < 256)
                throw new InvalidOperationException("Invalid public signing key");
            verificationKeys.Add(key.Kid, key);
            publicKeys.Add(element.Clone());
        }
    }

    public string Issue(string subject, DateTimeOffset? instant = null)
    {
        var now = (instant ?? DateTimeOffset.UtcNow).UtcDateTime;
        var token = handler.CreateJwtSecurityToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("jti", Guid.NewGuid().ToString()),
                new Claim("scope", "customer")]),
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddSeconds(settings.TokenLifetimeSeconds),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        });
        return handler.WriteToken(token);
    }

    public ClaimsPrincipal Verify(string token)
    {
        try { return VerifyCore(token); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException)
        { throw new SecurityTokenException("Malformed token"); }
    }

    private ClaimsPrincipal VerifyCore(string token)
    {
        if (token.Length > 8192) throw new SecurityTokenException("Token too large");
        var principal = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeyResolver = (_, _, kid, _) =>
                kid is not null && verificationKeys.TryGetValue(kid, out var key) ? [key] : []
        }, out var validated);
        var jwt = (JwtSecurityToken)validated;
        string[] required = ["sub", "iat", "exp", "iss", "aud", "jti"];
        if (required.Any(key => !jwt.Payload.ContainsKey(key)) || principal.FindFirst("scope")?.Value != "customer"
            || string.IsNullOrWhiteSpace(principal.FindFirst("sub")?.Value)
            || string.IsNullOrWhiteSpace(principal.FindFirst("jti")?.Value)
            || jwt.ValidTo <= jwt.IssuedAt || jwt.ValidTo - jwt.IssuedAt > TimeSpan.FromMinutes(15)
            || jwt.IssuedAt > DateTime.UtcNow)
            throw new SecurityTokenException("Invalid customer claims");
        return principal;
    }

    public object Jwks() => new { keys = publicKeys };
    public void Dispose() => rsa.Dispose();
}
