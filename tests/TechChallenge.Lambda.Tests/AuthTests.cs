using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace TechChallenge.Lambda.Tests;

public sealed class AuthFixture : IDisposable
{
    public Settings Settings { get; }
    public JwtTokens Tokens { get; }
    public FakeRepository Repository { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public AuthService Service { get; }
    public List<string> Logs { get; } = [];
    public Function Function { get; }
    public AuthFixture()
    {
        using var rsa = RSA.Create(2048);
        Settings = new Settings
        {
            DatabaseConnection = "Host=localhost",
            PrivateKey = rsa.ExportPkcs8PrivateKeyPem(),
            OtpSecret = "test-only-hmac-secret-32-characters",
            Environment = "test"
        };
        Tokens = new JwtTokens(Settings);
        Service = new AuthService(Settings, Repository, Notifier, Tokens);
        Function = new Function(() => Task.FromResult(Service), Logs.Add);
    }
    public static JsonElement Event(string path = "/auth/login", object? body = null, string method = "POST", string? token = null)
    {
        var headers = new Dictionary<string, string> { ["content-type"] = "application/json", ["x-correlation-id"] = "test-123" };
        if (token is not null) headers["authorization"] = "Bearer " + token;
        return JsonSerializer.SerializeToElement(new
        {
            rawPath = path,
            headers,
            body = JsonSerializer.Serialize(body ?? new { cpf = "52998224725" }),
            requestContext = new { http = new { method, sourceIp = "192.0.2.1" } }
        });
    }
    public void Dispose() => Tokens.Dispose();
}

public sealed class FakeNotifier : INotifier
{
    public string? Code { get; private set; }
    public int Sends { get; private set; }
    public bool Fail { get; set; }
    public Task SendAsync(string destination, string code, CancellationToken cancellation)
    {
        if (Fail) throw new InvalidOperationException("private credential");
        Code = code; Sends++;
        return Task.CompletedTask;
    }
}

public sealed class FakeRepository : IClientRepository
{
    public Dictionary<string, (Challenge Value, int Attempts, bool Consumed)> Challenges { get; } = [];
    private readonly Dictionary<string, int> counts = [];
    public bool Active { get; set; } = true;
    public Exception? Failure { get; set; }
    public Task<Client?> FindAsync(string cpf, CancellationToken cancellation) => Task.FromResult(cpf switch
    {
        "52998224725" => new Client("customer-id", Active, "customer@example.test"),
        "11144477735" => new Client("inactive-id", false, "inactive@example.test"),
        _ => null
    });
    public Task<bool> ReserveAsync(string key, long window, int limit, CancellationToken cancellation)
    {
        if (Failure is not null) throw Failure;
        var bucket = key + window;
        counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
        return Task.FromResult(counts[bucket] <= limit);
    }
    public Task CreateChallengeAsync(Challenge value, CancellationToken cancellation)
    { Challenges.Add(value.Id, (value, 0, false)); return Task.CompletedTask; }
    public Task<string?> ConsumeAsync(string id, string verifier, long now, CancellationToken cancellation)
    {
        if (!Challenges.TryGetValue(id, out var row) || row.Consumed || row.Attempts >= 5 || row.Value.ExpiresAt <= now)
            return Task.FromResult<string?>(null);
        var valid = row.Value.Verifier == verifier;
        Challenges[id] = (row.Value, row.Attempts + 1, valid);
        return Task.FromResult(valid && Active ? row.Value.ClientId : null);
    }
    public Task CleanupAsync(long now, CancellationToken cancellation) => Task.CompletedTask;
}

public sealed class CpfTests
{
    [Theory]
    [InlineData("52998224725")]
    [InlineData("529.982.247-25")]
    [InlineData("11144477735")]
    public void Valid(string value) => Assert.Equal(11, Cpf.Normalize(value).Length);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("5299822472")]
    [InlineData("529982247250")]
    [InlineData("52998224724")]
    [InlineData("52998224715")]
    [InlineData("529a98224725")]
    [InlineData("529-982-247.25")]
    [InlineData("５２９９８２２４７２５")]
    [InlineData("52998224725\n")]
    [InlineData("' OR 1=1--")]
    [InlineData("00000000000")]
    [InlineData("11111111111")]
    [InlineData("22222222222")]
    [InlineData("33333333333")]
    [InlineData("44444444444")]
    [InlineData("55555555555")]
    [InlineData("66666666666")]
    [InlineData("77777777777")]
    [InlineData("88888888888")]
    [InlineData("99999999999")]
    public void Invalid(string? value) => Assert.Throws<AuthException>(() => Cpf.Normalize(value));
}

public sealed class AuthTests
{
    [Fact]
    public async Task FullFlowAndReplay()
    {
        using var f = new AuthFixture();
        var login = await f.Function.FunctionHandler(AuthFixture.Event(), null);
        Assert.Equal(202, login.StatusCode);
        var id = JsonDocument.Parse(login.Body).RootElement.GetProperty("challenge_id").GetString();
        var payload = new { challenge_id = id, code = f.Notifier.Code };
        var verify = await f.Function.FunctionHandler(AuthFixture.Event("/auth/verify", payload), null);
        Assert.Equal(200, verify.StatusCode);
        var token = JsonDocument.Parse(verify.Body).RootElement.GetProperty("access_token").GetString()!;
        Assert.Equal("customer-id", f.Tokens.Verify(token).FindFirst("sub")!.Value);
        Assert.Equal(200, (await f.Function.FunctionHandler(AuthFixture.Event("/auth/me", method: "GET", token: token), null)).StatusCode);
        Assert.Equal(401, (await f.Function.FunctionHandler(AuthFixture.Event("/auth/verify", payload), null)).StatusCode);
        var logs = string.Join('\n', f.Logs);
        Assert.DoesNotContain("52998224725", logs);
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain("private credential", logs);
        Assert.All(f.Logs, value => Assert.Equal("test-123", JsonDocument.Parse(value).RootElement.GetProperty("correlation_id").GetString()));
        Assert.NotEqual(f.Notifier.Code, f.Repository.Challenges[id!].Value.Verifier);
    }

    [Theory]
    [InlineData("11144477735")]
    [InlineData("12345678909")]
    public async Task UnknownAndInactiveHaveSameResponse(string cpf)
    {
        using var f = new AuthFixture();
        var response = await f.Function.FunctionHandler(AuthFixture.Event(body: new { cpf }), null);
        Assert.Equal(202, response.StatusCode);
        Assert.Equal(0, f.Notifier.Sends);
        Assert.Empty(f.Repository.Challenges);
        Assert.True(Guid.TryParse(JsonDocument.Parse(response.Body).RootElement.GetProperty("challenge_id").GetString(), out _));
    }

    [Fact]
    public async Task AttemptsAreLimited()
    {
        using var f = new AuthFixture();
        var login = await f.Service.LoginAsync("52998224725", "ip", default);
        var wrong = f.Notifier.Code == "000000" ? "999999" : "000000";
        for (var i = 0; i < 5; i++) await Assert.ThrowsAsync<AuthException>(() => f.Service.VerifyAsync(login.ChallengeId, wrong, "ip", default));
        await Assert.ThrowsAsync<AuthException>(() => f.Service.VerifyAsync(login.ChallengeId, f.Notifier.Code!, "ip", default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpiredOrDisabledCannotAuthenticate(bool expired)
    {
        using var f = new AuthFixture();
        var login = await f.Service.LoginAsync("52998224725", "ip", default);
        if (expired)
        {
            var row = f.Repository.Challenges[login.ChallengeId];
            f.Repository.Challenges[login.ChallengeId] = (row.Value with { ExpiresAt = 1 }, 0, false);
        }
        else f.Repository.Active = false;
        await Assert.ThrowsAsync<AuthException>(() => f.Service.VerifyAsync(login.ChallengeId, f.Notifier.Code!, "ip", default));
    }

    [Fact]
    public async Task RateLimitSurvivesIpChange()
    {
        using var f = new AuthFixture();
        for (var i = 0; i < 5; i++) await f.Service.LoginAsync("52998224725", "ip" + i, default);
        var error = await Assert.ThrowsAsync<AuthException>(() => f.Service.LoginAsync("52998224725", "other-ip", default));
        Assert.Equal(429, error.Status);
    }

    [Fact]
    public async Task DeliveryFailureDoesNotRevealCustomer()
    {
        using var f = new AuthFixture();
        f.Notifier.Fail = true;
        var response = await f.Function.FunctionHandler(AuthFixture.Event(), null);
        Assert.Equal(202, response.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(f.Logs.Single()).RootElement.GetProperty("NotificationErrors").GetInt32());
        Assert.DoesNotContain("private credential", response.Body + f.Logs.Single());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"cpf\":1}")]
    [InlineData("{\"cpf\":\"52998224725\",\"role\":\"admin\"}")]
    [InlineData("{\"cpf\":\"52998224725\",\"cpf\":\"11144477735\"}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task MalformedPayloadRejected(string raw)
    {
        using var f = new AuthFixture();
        var input = JsonSerializer.SerializeToElement(new
        {
            rawPath = "/auth/login",
            body = raw,
            headers = new { content_type = "application/json" },
            requestContext = new { http = new { method = "POST", sourceIp = "ip" } }
        });
        // Use exact HTTP header name in JSON.
        input = JsonDocument.Parse(input.GetRawText().Replace("content_type", "content-type")).RootElement;
        Assert.Equal(400, (await f.Function.FunctionHandler(input, null)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/auth/me", 401)]
    [InlineData("POST", "/unknown", 404)]
    [InlineData("GET", "/.well-known/jwks.json", 200)]
    [InlineData("GET", "/.well-known/openid-configuration", 200)]
    public async Task Routing(string method, string path, int expected)
    {
        using var f = new AuthFixture();
        Assert.Equal(expected, (await f.Function.FunctionHandler(AuthFixture.Event(path, method: method), null)).StatusCode);
    }

    [Fact]
    public async Task DatabaseFailureIsSanitized()
    {
        using var f = new AuthFixture();
        f.Repository.Failure = new Npgsql.NpgsqlException("private SQL credential");
        var response = await f.Function.FunctionHandler(AuthFixture.Event(), null);
        Assert.Equal(503, response.StatusCode);
        Assert.DoesNotContain("private SQL", response.Body + f.Logs.Single());
    }

    [Fact]
    public async Task OversizedRequestRejected()
    {
        using var f = new AuthFixture();
        Assert.Equal(413, (await f.Function.FunctionHandler(AuthFixture.Event(body: new { cpf = new string('a', 9000) }), null)).StatusCode);
    }
}

public sealed class EdgeCaseTests
{
    [Theory]
    [InlineData("invalid-token")]
    [InlineData("a.b.c")]
    public async Task MalformedTokenIsUnauthorized(string token)
    {
        using var f = new AuthFixture();
        Assert.Equal(401, (await f.Function.FunctionHandler(AuthFixture.Event("/auth/me", method: "GET", token: token), null)).StatusCode);
    }

    [Fact]
    public async Task TimeoutReturnsSanitized503()
    {
        using var f = new AuthFixture();
        f.Repository.Failure = new OperationCanceledException("sensitive");
        var response = await f.Function.FunctionHandler(AuthFixture.Event(), null);
        Assert.Equal(503, response.StatusCode);
        Assert.Contains("request_timeout", response.Body);
        Assert.DoesNotContain("sensitive", response.Body + f.Logs.Single());
    }

    [Theory]
    [InlineData(false, "text/plain", 415)]
    [InlineData(true, "application/json", 400)]
    public async Task MediaTypeAndBase64Validation(bool encoded, string contentType, int expected)
    {
        using var f = new AuthFixture();
        var input = JsonSerializer.SerializeToElement(new
        {
            rawPath = "/auth/login",
            body = "%%%",
            isBase64Encoded = encoded,
            headers = new Dictionary<string, string> { ["content-type"] = contentType },
            requestContext = new { http = new { method = "POST", sourceIp = "ip" } }
        });
        Assert.Equal(expected, (await f.Function.FunctionHandler(input, null)).StatusCode);
    }

    [Fact]
    public async Task Base64JsonAccepted()
    {
        using var f = new AuthFixture();
        var input = JsonSerializer.SerializeToElement(new
        {
            rawPath = "/auth/login",
            body = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"cpf\":\"52998224725\"}")),
            isBase64Encoded = true,
            headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            requestContext = new { http = new { method = "POST", sourceIp = "ip" } }
        });
        Assert.Equal(202, (await f.Function.FunctionHandler(input, null)).StatusCode);
    }

    [Fact]
    public async Task ScheduledCleanupAccepted()
    {
        using var f = new AuthFixture();
        Assert.Equal(200, (await f.Function.FunctionHandler(JsonSerializer.SerializeToElement(new { source = "aws.events" }), null)).StatusCode);
    }

    [Fact]
    public async Task NoOtpCannotIssueToken()
    {
        using var f = new AuthFixture();
        var response = await f.Function.FunctionHandler(AuthFixture.Event("/auth/verify", new { challenge_id = Guid.NewGuid(), code = "123456" }), null);
        Assert.Equal(401, response.StatusCode);
        Assert.DoesNotContain("access_token", response.Body);
    }
}
