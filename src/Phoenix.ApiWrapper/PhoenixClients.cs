using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Phoenix.ApiWrapper.Entities;

namespace Phoenix.ApiWrapper;

public sealed class PhoenixClients
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri s_pnBaseUrl = new Uri("https://api.phoenixnetwork.net");

    private readonly HttpClient _oauthHttp;
    private readonly PhoenixApiClientOptions? _pnOptions;

    private readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new();

    /// <summary>
    /// The main way to interface with the Phoenix api.
    /// </summary>
    public Phoenix.Api.ApiClient? PhoenixClient { get; }

    public PhoenixClients(HttpClient oauthHttpClient, PhoenixApiClientOptions? pnOptions)
    {
        _oauthHttp = oauthHttpClient ?? throw new ArgumentNullException(nameof(oauthHttpClient));
        _pnOptions = pnOptions ?? throw new ArgumentNullException(nameof(pnOptions));

        _pnOptions = pnOptions;

        if (_pnOptions.TokenEndpoint is null)
        {
            throw new ArgumentException("TokenEndpoint must be configured.", nameof(pnOptions));
        }

        if (string.IsNullOrWhiteSpace(_pnOptions.ClientId))
        {
            throw new ArgumentException("ClientId must be configured.", nameof(pnOptions));
        }

        if (string.IsNullOrWhiteSpace(_pnOptions.ClientSecret))
        {
            throw new ArgumentException("ClientSecret must be configured.", nameof(pnOptions));
        }

        if (_pnOptions.EnableTokenExchange)
        {
            if (string.IsNullOrEmpty(_pnOptions.SubjectId) || string.IsNullOrEmpty(_pnOptions.SubjectProvider))
            {
                throw new ArgumentException("SubjectId and SubjectProvider need to be configured", nameof(pnOptions));
            }

            PhoenixClient = new Phoenix.Api.ApiClient(CreateKiotaAdapterOnBehalfOf(_pnOptions.SubjectId, _pnOptions.SubjectProvider, _pnOptions.Scopes, audience: null));
        }
        else
        {
            PhoenixClient = new Phoenix.Api.ApiClient(CreateKiotaAdapterForClientCredentials(_pnOptions.Scopes));
        }
    }

    /// <summary>
    /// Typed Kiota client for the GalaxyLife API using token exchange (on-behalf-of).
    /// </summary>
    public Phoenix.Api.ApiClient PhoenixOnBehalfOf(string subjectId, string subjectProvider) =>
        new(CreateKiotaAdapterOnBehalfOf(subjectId, subjectProvider, _pnOptions.Scopes, audience: null));

    /// <summary>
    /// Gets an access token using OAuth2 client credentials grant.
    /// Tokens are cached until shortly before expiry.
    /// </summary>
    public Task<AccessToken> GetClientCredentialsTokenAsync(
        IEnumerable<string>? scopes = null,
        CancellationToken cancellationToken = default)
    {
        var scopeString = NormalizeScopes(scopes ?? _pnOptions.DefaultScopes);
        var cacheKey = $"cc|{scopeString}";

        return GetOrCreateTokenAsync(
            cacheKey,
            () => RequestClientCredentialsTokenAsync(scopeString, cancellationToken));
    }

    /// <summary>
    /// Exchanges a subject token for a new access token (RFC 8693).
    /// Only allowed if EnableTokenExchange is set for this client.
    /// </summary>
    public Task<AccessToken> ExchangeOnBehalfOfAsync(
        string subjectId,
        string subjectProvider,
        IEnumerable<string>? scopes = null,
        string? audience = null,
        CancellationToken cancellationToken = default)
    {
        if (!_pnOptions.EnableTokenExchange)
            throw new InvalidOperationException("Token exchange is not enabled for this client configuration.");

        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("Subject id must be provided.", nameof(subjectId));

        var scopeString = NormalizeScopes(scopes ?? _pnOptions.DefaultScopes);
        var subjectHash = StableHash(subjectId);
        var cacheKey = $"xchg|{scopeString}|{audience}|{subjectHash}";

        return GetOrCreateTokenAsync(
            cacheKey,
            () => RequestTokenExchangeAsync(subjectId, subjectProvider, scopeString, audience, cancellationToken));
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
        string subjectId,
        string subjectProvider,
        IEnumerable<string>? scopes = null,
        string? audience = null,
        CancellationToken cancellationToken = default)
    {
        var token = await ExchangeOnBehalfOfAsync(subjectId, subjectProvider, scopes, audience, cancellationToken).ConfigureAwait(false);

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
    private IRequestAdapter CreateKiotaAdapterForClientCredentials(IEnumerable<string>? scopes)
    {
        var tokenProvider = new KiotaAccessTokenProvider(
            acquireTokenAsync: ct => GetClientCredentialsTokenAsync(scopes, ct),
            allowedHosts: GetAllowedHosts(s_pnBaseUrl));

        var authProvider = new BaseBearerTokenAuthenticationProvider(tokenProvider);
        var adapter = new HttpClientRequestAdapter(authProvider)
        {
            BaseUrl = s_pnBaseUrl.ToString().TrimEnd('/')
        };

        return adapter;
    }

    private IRequestAdapter CreateKiotaAdapterOnBehalfOf(string subjectId, string subjectProvider, IEnumerable<string>? scopes, string? audience)
    {
        var tokenProvider = new KiotaAccessTokenProvider(
            acquireTokenAsync: ct => ExchangeOnBehalfOfAsync(subjectId, subjectProvider, scopes, audience, ct),
            allowedHosts: GetAllowedHosts(s_pnBaseUrl));

        var authProvider = new BaseBearerTokenAuthenticationProvider(tokenProvider);
        var adapter = new HttpClientRequestAdapter(authProvider)
        {
            BaseUrl = s_pnBaseUrl.ToString().TrimEnd('/')
        };

        return adapter;
    }

    private string[] GetAllowedHosts(Uri apiBaseUrl) =>
        _pnOptions.AllowedHosts is { Length: > 0 }
            ? _pnOptions.AllowedHosts
            : [apiBaseUrl.Host];

    // --------------------
    // Token plumbing
    // --------------------
    private async Task<AccessToken> GetOrCreateTokenAsync(
        string cacheKey,
        Func<Task<AccessToken>> factory)
    {
        if (_tokenCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired(_pnOptions.ExpirySkew))
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
            ["client_id"] = _pnOptions.ClientId,
            ["client_secret"] = _pnOptions.ClientSecret,
        };

        if (!string.IsNullOrWhiteSpace(scopeString))
            form["scope"] = scopeString;

        return await RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccessToken> RequestTokenExchangeAsync(
        string subjectId,
        string subjectProvider,
        string scopeString,
        string? audience,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "token_exchange",
            ["client_id"] = _pnOptions.ClientId,
            ["client_secret"] = _pnOptions.ClientSecret,
            ["subject_token"] = (await GetClientCredentialsTokenAsync()).Value,
            ["subject_token_type"] = _pnOptions.SubjectTokenType,
            ["subject_id"] = subjectId,
            ["subject_provider"] = subjectProvider,
        };

        if (!string.IsNullOrWhiteSpace(scopeString))
            form["scope"] = scopeString;

        if (!string.IsNullOrWhiteSpace(audience))
            form["audience"] = audience;

        if (!string.IsNullOrWhiteSpace(_pnOptions.RequestedTokenType))
            form["requested_token_type"] = _pnOptions.RequestedTokenType;

        return await RequestTokenAsync(form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccessToken> RequestTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _pnOptions.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _oauthHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Token endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}). Payload: {payload}");
        }

        var token = JsonSerializer.Deserialize<TokenEndpointResponse>(payload, s_jsonOptions);

        if (token == null)
        {
            throw new InvalidOperationException("Token endpoint returned an empty response.");
        }

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Token endpoint response did not include access_token.");
        }

        var expiresInSeconds = token.ExpiresIn <= 0 ? _pnOptions.FallbackExpiresInSeconds : token.ExpiresIn;
        var expiresAt = DateTime.UtcNow.AddSeconds(expiresInSeconds);

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
            var hash = 23;

            foreach (var ch in value)
            {
                hash = (hash * 31) + ch;
            }

            return hash.ToString("X", CultureInfo.InvariantCulture);
        }
    }
}
