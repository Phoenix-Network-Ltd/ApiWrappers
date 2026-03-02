namespace Phoenix.ApiWrapper.Entities;

public sealed class PhoenixApiClientOptions
{
    public Uri? TokenEndpoint { get; init; }

    public string ClientId { get; init; } = "";

    public string ClientSecret { get; init; } = "";

    /// <summary>Default scopes used when none are provided to methods.</summary>
    public string[] DefaultScopes { get; init; } = [];

    /// <summary>Enable RFC 8693 token exchange flows (only for allowed clients).</summary>
    public bool EnableTokenExchange { get; init; }

    /// <summary>Defaults to access token type if not set.</summary>
    public string? SubjectTokenType { get; init; }

    /// <summary>Optional requested_token_type (RFC 8693).</summary>
    public string? RequestedTokenType { get; init; }

    /// <summary>How early we refresh before the token actually expires.</summary>
    public TimeSpan ExpirySkew { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>If expires_in is missing/invalid, use this.</summary>
    public int FallbackExpiresInSeconds { get; init; } = 300;

    public string[] AllowedHosts { get; init; } = [];

    public string[] Scopes { get; init; } = [];

    public string? SubjectId { get; set; }

    public string? SubjectProvider { get; set; }
}
