using Microsoft.Kiota.Abstractions.Authentication;
using Phoenix.ApiWrapper.Entities;

namespace Phoenix.ApiWrapper;

public class KiotaAccessTokenProvider : IAccessTokenProvider
{
    private readonly Func<CancellationToken, Task<AccessToken>> _acquireTokenAsync;

    public KiotaAccessTokenProvider(
        Func<CancellationToken, Task<AccessToken>> acquireTokenAsync,
        IEnumerable<string>? allowedHosts)
    {
        _acquireTokenAsync = acquireTokenAsync ?? throw new ArgumentNullException(nameof(acquireTokenAsync));
        AllowedHostsValidator = new AllowedHostsValidator();
        if (allowedHosts is not null)
            AllowedHostsValidator.AllowedHosts = allowedHosts.ToArray();
    }

    public AllowedHostsValidator AllowedHostsValidator { get; }

    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        var token = await _acquireTokenAsync(cancellationToken).ConfigureAwait(false);
        return token.Value;
    }
}
