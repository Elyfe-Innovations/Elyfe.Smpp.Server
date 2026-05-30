namespace Elyfe.Smpp.Server.Authentication;

/// <summary>
///     A configured ESME account used by the file- and configuration-based authenticators.
/// </summary>
public sealed class SmppAccount
{
    /// <summary>The ESME <c>system_id</c> (login).</summary>
    public string SystemId { get; set; } = string.Empty;

    /// <summary>
    ///     The account password in plain text. Prefer <see cref="PasswordSha256" /> in production; this is provided for
    ///     local development convenience only.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    ///     The SHA-256 hash (lower-case hex) of the account password. Takes precedence over <see cref="Password" />
    ///     when both are set.
    /// </summary>
    public string? PasswordSha256 { get; set; }

    /// <summary>The tenant this account maps to. Required.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Whether the account is enabled. Disabled accounts are rejected at bind time.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
///     Options for the static credential authenticators.
/// </summary>
public sealed class SmppAuthenticationOptions
{
    /// <summary>The configuration section name used when binding from <c>appsettings.json</c>.</summary>
    public const string SectionName = "SmppServer:Authentication";

    /// <summary>The configured ESME accounts.</summary>
    public IList<SmppAccount> Accounts { get; set; } = [];
}
