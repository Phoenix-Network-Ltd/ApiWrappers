namespace Phoenix.ApiWrapper.Entities;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAtUtc, string TokenType, string? Scope);

public sealed record CachedToken(AccessToken Token)
{
    public bool IsExpired(TimeSpan skew) => DateTimeOffset.UtcNow >= Token.ExpiresAtUtc.Subtract(skew);
}
