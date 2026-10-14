using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace TechChallenge.Lambda;

public sealed record Client(string Id, bool Active, string Email);
public sealed record Challenge(string Id, string ClientId, string Verifier, long ExpiresAt);

public interface IClientRepository
{
    Task<Client?> FindAsync(string cpf, CancellationToken cancellation);
    Task<bool> ReserveAsync(string key, long window, int limit, CancellationToken cancellation);
    Task CreateChallengeAsync(Challenge challenge, CancellationToken cancellation);
    Task<string?> ConsumeAsync(string id, string verifier, long now, CancellationToken cancellation);
    Task CleanupAsync(long now, CancellationToken cancellation);
}

public sealed class ClientRepository : IClientRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;

    public ClientRepository(string connectionString)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString)
        { MaxPoolSize = 2, Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false };
        dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
    }

    public async Task<Client?> FindAsync(string cpf, CancellationToken cancellation)
    {
        await using var command = dataSource.CreateCommand("SELECT id, active, email FROM auth_clients WHERE cpf = $1");
        command.Parameters.AddWithValue(cpf);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation)
            ? new Client(reader.GetString(0), reader.GetBoolean(1), reader.GetString(2)) : null;
    }

    public async Task<bool> ReserveAsync(string key, long window, int limit, CancellationToken cancellation)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO auth_rate_limits (key, "window", count) VALUES ($1, $2, 1)
            ON CONFLICT (key, "window") DO UPDATE SET count = auth_rate_limits.count + 1 RETURNING count
            """);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(window);
        return (int)(await command.ExecuteScalarAsync(cancellation))! <= limit;
    }

    public async Task CreateChallengeAsync(Challenge challenge, CancellationToken cancellation)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO auth_challenges (id, client_id, verifier, expires_at, attempts, consumed)
            VALUES ($1, $2, $3, $4, 0, false)
            """);
        command.Parameters.AddWithValue(challenge.Id);
        command.Parameters.AddWithValue(challenge.ClientId);
        command.Parameters.AddWithValue(challenge.Verifier);
        command.Parameters.AddWithValue(challenge.ExpiresAt);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task<string?> ConsumeAsync(string id, string verifier, long now, CancellationToken cancellation)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await using var read = new NpgsqlCommand("""
            SELECT client_id, verifier, expires_at, attempts, consumed FROM auth_challenges WHERE id = $1 FOR UPDATE
            """, connection, transaction);
        read.Parameters.AddWithValue(id);
        string clientId, stored;
        long expiration;
        int attempts;
        bool consumed;
        await using (var reader = await read.ExecuteReaderAsync(cancellation))
        {
            if (!await reader.ReadAsync(cancellation)) return null;
            clientId = reader.GetString(0); stored = reader.GetString(1); expiration = reader.GetInt64(2);
            attempts = reader.GetInt32(3); consumed = reader.GetBoolean(4);
        }
        if (consumed || expiration <= now || attempts >= 5) return null;
        var valid = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(verifier));
        await using var update = new NpgsqlCommand("""
            UPDATE auth_challenges SET attempts = attempts + 1, consumed = $2 WHERE id = $1
            """, connection, transaction);
        update.Parameters.AddWithValue(id); update.Parameters.AddWithValue(valid);
        await update.ExecuteNonQueryAsync(cancellation);
        string? subject = null;
        if (valid)
        {
            await using var client = new NpgsqlCommand("SELECT id FROM auth_clients WHERE id = $1 AND active = true", connection, transaction);
            client.Parameters.AddWithValue(clientId);
            subject = await client.ExecuteScalarAsync(cancellation) as string;
        }
        // Commit invalid attempts as well: throwing before commit would undo the brute-force limit.
        await transaction.CommitAsync(cancellation);
        return subject;
    }

    public async Task CleanupAsync(long now, CancellationToken cancellation)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM auth_challenges WHERE expires_at < $1");
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(cancellation);
        await using var buckets = dataSource.CreateCommand("DELETE FROM auth_rate_limits WHERE \"window\" < $1");
        buckets.Parameters.AddWithValue(now / 300 - 1);
        await buckets.ExecuteNonQueryAsync(cancellation);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
