namespace Elyfe.Smpp.Server.Authentication;

/// <summary>
///     The outcome of an <see cref="IAuthenticator.AuthenticateAsync" /> call.
/// </summary>
public sealed class AuthenticationResult
{
    private AuthenticationResult(bool succeeded, string? tenantId, string? failureReason)
    {
        Succeeded = succeeded;
        TenantId = tenantId;
        FailureReason = failureReason;
    }

    /// <summary>
    ///     Whether the bind credentials were accepted.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    ///     The tenant the authenticated <c>system_id</c> maps to. Used by the bridge for routing and billing.
    ///     <c>null</c> when authentication failed.
    /// </summary>
    public string? TenantId { get; }

    /// <summary>
    ///     A human-readable reason for a failed authentication, for diagnostics. <c>null</c> on success.
    /// </summary>
    public string? FailureReason { get; }

    /// <summary>
    ///     Creates a successful result associated with the supplied tenant.
    /// </summary>
    public static AuthenticationResult Success(string tenantId) => new(true, tenantId, null);

    /// <summary>
    ///     Creates a failed result with an optional diagnostic reason.
    /// </summary>
    public static AuthenticationResult Fail(string? reason = null) => new(false, null, reason);
}
