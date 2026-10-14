using System.Security.Cryptography;
using System.Text;

namespace TechChallenge.Lambda;

public sealed record LoginResult(string ChallengeId, bool DeliveryFailed);

public sealed class AuthService(Settings settings, IClientRepository repository, INotifier notifier, JwtTokens tokens,
    TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    public Settings Settings { get; } = settings;
    public JwtTokens Tokens { get; } = tokens;

    private string Digest(string value) => Convert.ToHexStringLower(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(Settings.OtpSecret), Encoding.UTF8.GetBytes(value)));

    private async Task LimitAsync(string dimension, int limit, CancellationToken cancellation)
    {
        if (!await repository.ReserveAsync(Digest(dimension), clock.GetUtcNow().ToUnixTimeSeconds() / 300, limit, cancellation))
            throw new AuthException("rate_limited", 429);
    }

    public async Task<LoginResult> LoginAsync(string? cpf, string sourceIp, CancellationToken cancellation)
    {
        await LimitAsync("login-ip:" + sourceIp, 30, cancellation);
        cpf = Cpf.Normalize(cpf);
        await LimitAsync("login-cpf:" + cpf, 5, cancellation);
        var client = await repository.FindAsync(cpf, cancellation);
        var challenge = Guid.NewGuid().ToString();
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
        var deliveryFailed = false;
        if (client is { Active: true })
        {
            await repository.CreateChallengeAsync(new Challenge(challenge, client.Id,
                Digest($"otp:{challenge}:{code}"), clock.GetUtcNow().ToUnixTimeSeconds() + 300), cancellation);
            try { await notifier.SendAsync(client.Email, code, cancellation); }
            catch (Exception) when (!cancellation.IsCancellationRequested) { deliveryFailed = true; }
        }
        return new LoginResult(challenge, deliveryFailed);
    }

    public async Task<string> VerifyAsync(string challengeId, string code, string sourceIp, CancellationToken cancellation)
    {
        await LimitAsync("verify-ip:" + sourceIp, 30, cancellation);
        var subject = await repository.ConsumeAsync(challengeId, Digest($"otp:{challengeId}:{code}"),
            clock.GetUtcNow().ToUnixTimeSeconds(), cancellation);
        if (subject is null) throw new AuthException("invalid_challenge", 401);
        return Tokens.Issue(subject);
    }

    public Task CleanupAsync(CancellationToken cancellation) =>
        repository.CleanupAsync(clock.GetUtcNow().ToUnixTimeSeconds(), cancellation);
}
