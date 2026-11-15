namespace AssetInventory.Application.Auth;

public sealed record AuthAttemptDecision(bool Allowed, int RetryAfterSeconds);
public interface IAuthAttemptLimiter
{
    Task<AuthAttemptDecision> CheckAsync(string login, CancellationToken ct);
}
