using Microsoft.Extensions.Options;

namespace Elyfe.Smpp.Server.Authentication;

/// <summary>
///     An <see cref="IAuthenticator" /> that validates bind credentials against accounts supplied through
///     <see cref="SmppAuthenticationOptions" /> (e.g. <c>appsettings.json</c> or environment variables).
/// </summary>
public sealed class ConfigBasedAuthenticator : IAuthenticator
{
    private readonly IOptionsMonitor<SmppAuthenticationOptions> _options;

    /// <summary>Creates the authenticator over the supplied options.</summary>
    public ConfigBasedAuthenticator(IOptionsMonitor<SmppAuthenticationOptions> options) => _options = options;

    /// <inheritdoc />
    public Task<AuthenticationResult> AuthenticateAsync(SmppCredentials credentials, CancellationToken cancellationToken)
    {
        var account = _options.CurrentValue.Accounts
            .FirstOrDefault(a => string.Equals(a.SystemId, credentials.SystemId, StringComparison.Ordinal));
        return Task.FromResult(SmppAccountVerifier.Verify(account, credentials));
    }
}
