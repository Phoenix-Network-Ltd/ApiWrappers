using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace Phoenix.ApiWrapper;

public sealed class PhoenixApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _oauthHttp;
    private readonly PhoenixApiClientOptions _options;

    private readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new();
    
    private Phoenix.GalaxyLife.Api.ApiClient? _galaxyLife;

    public PhoenixApiClient(HttpClient oauthHttpClient, PhoenixApiClientOptions options)
    {
        _oauthHttp = oauthHttpClient ?? throw new ArgumentNullException(nameof(oauthHttpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (_options.TokenEndpoint is null)
            throw new ArgumentException("TokenEndpoint must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ArgumentException("ClientId must be configured.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ArgumentException("ClientSecret must be configured.", nameof(options));
    }
    
    /// <summary>
    /// Typed Kiota client for the GalaxyLife API using client-credentials.
    /// </summary>
    public Phoenix.GalaxyLife.Api.ApiClient GalaxyLife =>
        _galaxyLife ??= new Phoenix.GalaxyLife.Api.ApiClient(
            CreateKiotaAdapterForClientCredentials(_options.GalaxyLifeBaseUrl, _options.GalaxyLifeScopes)
        );

    /// <summary>
    /// Typed Kiota client for the GalaxyLife API using token exchange (on-behalf-of).
    /// </summary>
    public Phoenix.GalaxyLife.Api.ApiClient GalaxyLifeOnBehalfOf(string subjectToken) =>
        new(
            CreateKiotaAdapterOnBehalfOf(_options.GalaxyLifeBaseUrl!, subjectToken, _options.GalaxyLifeScopes, audience: null)
        );

    /// <summary>
    /// Gets an access token using OAuth2 client credentials grant.
    /// Tokens are cached until shortly before expiry.
    /// </summary>
    public Task<AccessToken> GetClientCredentialsTokenAsync(
        IEnumerable<string>? scopes = null,
        CancellationToken cancellationToken = default)
    {
        var scopeString = NormalizeScopes(scopes ?? _options.DefaultScopes);
        var cacheKey = $"cc|{scopeString}";
        return GetOrCreateTokenAsync(
            cacheKey,
            () => RequestClientCredentialsTokenAsync(scopeString, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Exchanges a subject token for a new access token (RFC 8693).
    /// Only allowed if EnableTokenExchange is set for this client.
    /// </summary>
    public Task<AccessToken> ExchangeOnBehalfOfAsync(
        string subjectToken,
        IEnumerable<string>? scopes = null,
        string? audience = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableTokenExchange)
            throw new InvalidOperationException("Token exchange is not enabled for this client configuration.");

        if (string.IsNullOrWhiteSpace(subjectToken))
            throw new ArgumentException("Subject token must be provided.", nameof(subjectToken));

        var scopeString = NormalizeScopes(scopes ?? _options.DefaultScopes);
        var subjectHash = StableHash(subjectToken);
        var cacheKey = $"xchg|{scopeString}|{audience}|{subjectHash}";

        return GetOrCreateTokenAsync(
            cacheKey,
            () => RequestTokenExchangeAsync(subjectToken, scopeString, audience, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Creates an HttpClient that already has Authorization: Bearer set.
    /// You can use this to call any API the token is valid for.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedHttpClientAsync(
        Uri baseAddress,
        IEnumerable<string>? scopes = null,
        CancellationToken cancellationToken = default)
    {
        var token = await GetClientCredentialsTokenAsync(scopes, cancellationToken).ConfigureAwait(false);

        var client = new HttpClient
        {
            BaseAddress = baseAddress
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    /// <summary>
    /// Same as CreateAuthenticatedHttpClientAsync but using token exchange.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedHttpClientOnBehalfOfAsync(
        Uri baseAddress,
        string subjectToken,
        IEnumerable<string>? scopes = null,
        string? audience = null,
        CancellationToken cancellationToken = default)
    {
        var token = await ExchangeOnBehalfOfAsync(subjectToken, scopes, audience, cancellationToken).ConfigureAwait(false);

        var client = new HttpClient
        {
            BaseAddress = baseAddress
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }
    
    // --------------------
    // Kiota adapter creation
    // --------------------

    private IRequestAdapter CreateKiotaAdapterForClientCredentials(Uri apiBaseUrl, IEnumerable<string>? scopes)
    {
        var tokenProvider = new KiotaAccessTokenProvider(
            acquireTokenAsync: ct => GetClientCredentialsTokenAsync(scopes, ct),
            allowedHosts: GetAllowedHosts(apiBaseUrl));

        var authProvider = new BaseBearerTokenAuthenticationProvider(tokenProvider);
        var adapter = new HttpClientRequestAdapter(authProvider)
        {
            BaseUrl = apiBaseUrl.ToString().TrimEnd('/')
        };

        return adapter;
    }
    
    private IRequestAdapter CreateKiotaAdapterOnBehalfOf(Uri apiBaseUrl, string subjectToken, IEnumerable<string>? scopes, string? audience)
    {
        var tokenProvider = new KiotaAccessTokenProvider(
            acquireTokenAsync: ct => ExchangeOnBehalfOfAsync(subjectToken, scopes, audience, ct),
            allowedHosts: GetAllowedHosts(apiBaseUrl));

        var authProvider = new BaseBearerTokenAuthenticationProvider(tokenProvider);
        var adapter = new HttpClientRequestAdapter(authProvider)
        {
            BaseUrl = apiBaseUrl.ToString().TrimEnd('/')
        };

        return adapter;
    }

    private string[] GetAllowedHosts(Uri apiBaseUrl) =>
        _options.AllowedHosts is { Length: > 0 }
            ? _options.AllowedHosts
            : [apiBaseUrl.Host];

    // --------------------
    // Token plumbing
    // --------------------

    private async Task<AccessToken> GetOrCreateTokenAsync(
        string cacheKey,
        Func<Task<AccessToken>> factory,
        CancellationToken cancellationToken)
    {
        if (_tokenCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired(_options.ExpirySkew))
            return cached.Token;

        // Single-flight per key could be added later (SemaphoreSlim per key).
        var token = await factory().ConfigureAwait(false);
        _tokenCache[cacheKey] = new CachedToken(token);
        return token;
    }

    private async Task<AccessToken> RequestClientCredentialsTokenAsync(string scopeString, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };

        if (!string.IsNullOrWhiteSpace(scopeString))
            form["scope"] = scopeString;

        return await RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccessToken> RequestTokenExchangeAsync(
        string subjectToken,
        string scopeString,
        string? audience,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = _options.SubjectTokenType ?? "urn:ietf:params:oauth:token-type:access_token",
        };

        if (!string.IsNullOrWhiteSpace(scopeString))
            form["scope"] = scopeString;

        if (!string.IsNullOrWhiteSpace(audience))
            form["audience"] = audience;

        if (!string.IsNullOrWhiteSpace(_options.RequestedTokenType))
            form["requested_token_type"] = _options.RequestedTokenType;

        return await RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccessToken> RequestTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _oauthHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Token endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}). Payload: {payload}");
        }

        var token = JsonSerializer.Deserialize<TokenEndpointResponse>(payload, JsonOptions)
                    ?? throw new InvalidOperationException("Token endpoint returned an empty response.");

        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException("Token endpoint response did not include access_token.");

        var expiresInSeconds = token.ExpiresIn <= 0 ? _options.FallbackExpiresInSeconds : token.ExpiresIn;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);

        return new AccessToken(token.AccessToken, expiresAt, token.TokenType ?? "Bearer", token.Scope);
    }

    private static string NormalizeScopes(IEnumerable<string>? scopes)
    {
        if (scopes is null) return string.Empty;

        var list = scopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        return string.Join(' ', list);
    }

    private static string StableHash(string value)
    {
        // Non-cryptographic stable hash to avoid storing full subject tokens in cache keys.
        // (If you need stronger privacy guarantees, use SHA-256.)
        unchecked
        {
            int hash = 23;
            foreach (var ch in value)
                hash = (hash * 31) + ch;
            return hash.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // --------------------
    // Models / options
    // --------------------

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
        
        public string[]? AllowedHosts { get; init; }
        public Uri? GalaxyLifeBaseUrl { get; init; }
        public string[] GalaxyLifeScopes { get; init; } = [];
    }

    public sealed record AccessToken(string Value, DateTimeOffset ExpiresAtUtc, string TokenType, string? Scope);

    private sealed record CachedToken(AccessToken Token)
    {
        public bool IsExpired(TimeSpan skew) => DateTimeOffset.UtcNow >= Token.ExpiresAtUtc.Subtract(skew);
    }

    private sealed class TokenEndpointResponse
    {
        public string? AccessToken { get; set; }
        public int ExpiresIn { get; set; }
        public string? TokenType { get; set; }
        public string? Scope { get; set; }

        // JSON property names (web defaults will map snake_case if configured accordingly in future,
        // but we keep this simple by matching common casing via JsonSerializerDefaults.Web)
        public string? access_token { get => AccessToken; set => AccessToken = value; }
        public int expires_in { get => ExpiresIn; set => ExpiresIn = value; }
        public string? token_type { get => TokenType; set => TokenType = value; }
        public string? scope { get => Scope; set => Scope = value; }
    }
}