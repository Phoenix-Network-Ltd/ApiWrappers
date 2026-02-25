namespace Phoenix.ApiWrapper.Entities;

public sealed record AccessToken(string Value, DateTime ExpiresAtUtc, string TokenType, string? Scope);

public sealed record CachedToken(AccessToken Token)
{
    public bool IsExpired(TimeSpan skew) => DateTime.UtcNow >= Token.ExpiresAtUtc.Subtract(skew);
}
