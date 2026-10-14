using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace TechChallenge.Lambda.Tests;

public sealed class JwtTests
{
    [Fact]
    public void ValidClaimsContainNoCpf()
    {
        using var f = new AuthFixture();
        var token = f.Tokens.Issue("customer-id");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("RS256", jwt.Header.Alg);
        Assert.Equal("local-1", jwt.Header.Kid);
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "cpf" or "email");
        Assert.Equal("customer-id", f.Tokens.Verify(token).FindFirst("sub")!.Value);
    }

    [Fact]
    public void ExpiredTokenRejected()
    {
        using var f = new AuthFixture();
        Assert.ThrowsAny<SecurityTokenException>(() => f.Tokens.Verify(f.Tokens.Issue("customer", DateTimeOffset.UtcNow.AddHours(-1))));
    }

    [Theory]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("sub")]
    [InlineData("jti")]
    [InlineData("iat")]
    [InlineData("exp")]
    [InlineData("scope")]
    public void MissingClaimsRejected(string missing)
    {
        using var f = new AuthFixture();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(f.Tokens.Issue("customer"));
        jwt.Payload.Remove(missing);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(f.Settings.PrivateKey);
        var token = new JwtSecurityToken(new JwtHeader(new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "local-1" }, "RS256")), jwt.Payload);
        Assert.ThrowsAny<SecurityTokenException>(() => f.Tokens.Verify(new JwtSecurityTokenHandler().WriteToken(token)));
    }

    [Theory]
    [InlineData("iss", "https://attacker.test")]
    [InlineData("aud", "other-api")]
    [InlineData("scope", "admin")]
    public void WrongClaimsRejected(string key, string value)
    {
        using var f = new AuthFixture();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(f.Tokens.Issue("customer"));
        jwt.Payload[key] = value;
        using var rsa = RSA.Create(); rsa.ImportFromPem(f.Settings.PrivateKey);
        var token = new JwtSecurityToken(new JwtHeader(new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "local-1" }, "RS256")), jwt.Payload);
        Assert.ThrowsAny<SecurityTokenException>(() => f.Tokens.Verify(new JwtSecurityTokenHandler().WriteToken(token)));
    }

    [Fact]
    public void TamperedTokenRejected()
    {
        using var f = new AuthFixture();
        var parts = f.Tokens.Issue("customer").Split('.');
        parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];
        Assert.ThrowsAny<SecurityTokenException>(() => f.Tokens.Verify(string.Join('.', parts)));
    }

    [Fact]
    public void SymmetricAlgorithmRejected()
    {
        using var f = new AuthFixture();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(f.Tokens.Issue("customer"));
        var token = new JwtSecurityToken(new JwtHeader(new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "local-1" }, "HS256")), jwt.Payload);
        Assert.ThrowsAny<SecurityTokenException>(() => f.Tokens.Verify(new JwtSecurityTokenHandler().WriteToken(token)));
    }

    [Fact]
    public void JwksContainsOnlyPublicMaterial()
    {
        using var f = new AuthFixture();
        var key = JsonSerializer.SerializeToElement(f.Tokens.Jwks()).GetProperty("keys")[0];
        Assert.False(key.TryGetProperty("d", out _));
        Assert.False(key.TryGetProperty("p", out _));
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
    }

    [Fact]
    public void CloudRequiresTls()
    {
        using var f = new AuthFixture();
        var config = new Settings
        {
            Environment = "production",
            DatabaseConnection = "Host=db;SSL Mode=Disable",
            PrivateKey = f.Settings.PrivateKey,
            OtpSecret = f.Settings.OtpSecret,
            Issuer = "https://auth.example.test",
            KeyId = "prod-1"
        };
        Assert.Throws<InvalidOperationException>(config.Validate);
    }
}

public sealed class RotationTests
{
    [Fact]
    public void PreviousPublicKeyVerifiesOldToken()
    {
        using var old = new AuthFixture();
        using var rsa = RSA.Create(2048);
        var settings = new Settings
        {
            DatabaseConnection = "Host=localhost",
            OtpSecret = old.Settings.OtpSecret,
            PrivateKey = rsa.ExportPkcs8PrivateKeyPem(),
            KeyId = "new-key",
            PreviousPublicJwks = JsonSerializer.Serialize(old.Tokens.Jwks())
        };
        using var current = new JwtTokens(settings);
        Assert.Equal("customer", current.Verify(old.Tokens.Issue("customer")).FindFirst("sub")!.Value);
        Assert.Equal(2, JsonSerializer.SerializeToElement(current.Jwks()).GetProperty("keys").GetArrayLength());
    }

    [Fact]
    public void PrivateMaterialCannotEnterPublicJwks()
    {
        using var old = new AuthFixture();
        var settings = new Settings
        {
            DatabaseConnection = "Host=localhost",
            OtpSecret = old.Settings.OtpSecret,
            PrivateKey = old.Settings.PrivateKey,
            PreviousPublicJwks = "{\"keys\":[{\"d\":\"secret\"}]}"
        };
        Assert.Throws<InvalidOperationException>(() => new JwtTokens(settings));
    }
}
