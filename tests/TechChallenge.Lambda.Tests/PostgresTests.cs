using Npgsql;

namespace TechChallenge.Lambda.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION")))
            Skip = "Set TEST_DATABASE_CONNECTION to run PostgreSQL integration tests.";
    }
}

public sealed class PostgresTests : IAsyncLifetime
{
    private string? connectionString;
    private readonly string schema = "test_auth_" + Guid.NewGuid().ToString("N");
    private ClientRepository repository = null!;
    public async Task InitializeAsync()
    {
        connectionString = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION");
        if (string.IsNullOrEmpty(connectionString)) return;
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin);
        await create.ExecuteNonQueryAsync();
        var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
        await using var connection = new NpgsqlConnection(scoped);
        await connection.OpenAsync();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "TechChallenge.Lambda.slnx"))) root = root.Parent!;
        var sql = File.ReadAllText(Path.Combine(root.FullName, "infra/local/001-reference.sql"))
            + File.ReadAllText(Path.Combine(root.FullName, "infra/002-auth-state.sql"));
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        repository = new ClientRepository(scoped);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(connectionString)) return;
        if (repository is not null) await repository.DisposeAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task QueriesAreParameterized()
    {
        Assert.True((await repository.FindAsync("52998224725", default))!.Active);
        Assert.False((await repository.FindAsync("11144477735", default))!.Active);
        Assert.Null(await repository.FindAsync("' OR 1=1--", default));
    }

    [PostgresFact]
    public async Task ConcurrentOtpHasExactlyOneWinner()
    {
        var id = Guid.NewGuid().ToString();
        await repository.CreateChallengeAsync(new Challenge(id, "00000000-0000-4000-8000-000000000001", new string('a', 64), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60), default);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repository.ConsumeAsync(id, new string('a', 64), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), default)));
        Assert.Single(results, r => r is not null);
    }

    [PostgresFact]
    public async Task ConcurrentRateLimitIsAtomic()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => repository.ReserveAsync(new string('b', 64), 1, 5, default)));
        Assert.Equal(5, results.Count(r => r));
    }

    [PostgresFact]
    public async Task FailedAttemptsCommitAndBlockCorrectCode()
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await repository.CreateChallengeAsync(new Challenge(id, "00000000-0000-4000-8000-000000000001", new string('a', 64), now + 60), default);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => repository.ConsumeAsync(id, new string('b', 64), now, default)));
        Assert.Null(await repository.ConsumeAsync(id, new string('a', 64), now, default));
    }

    [PostgresFact]
    public async Task CleanupRemovesExpiredChallenges()
    {
        var id = Guid.NewGuid().ToString();
        await repository.CreateChallengeAsync(new Challenge(id, "00000000-0000-4000-8000-000000000001", new string('a', 64), 1), default);
        await repository.CleanupAsync(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), default);
        Assert.Null(await repository.ConsumeAsync(id, new string('a', 64), 0, default));
    }
}
