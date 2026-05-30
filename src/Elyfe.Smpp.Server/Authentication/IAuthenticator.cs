namespace Elyfe.Smpp.Server.Authentication;

/// <summary>
///     Validates ESME bind credentials. Implementations are pluggable so the server can authenticate
///     against different backends (Zitadel PATs, a credentials file, or static configuration).
/// </summary>
public interface IAuthenticator
{
    /// <summary>
    ///     Validates the supplied bind credentials.
    /// </summary>
    /// <param name="credentials">The <c>system_id</c> / <c>password</c> presented at bind time.</param>
    /// <param name="cancellationToken">A token to cancel the (potentially remote) validation call.</param>
    /// <returns>
    ///     An <see cref="AuthenticationResult" /> indicating success (with the resolved tenant id) or failure.
    /// </returns>
    Task<AuthenticationResult> AuthenticateAsync(SmppCredentials credentials, CancellationToken cancellationToken = default);
}
