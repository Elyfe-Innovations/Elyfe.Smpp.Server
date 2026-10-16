namespace Elyfe.Smpp.Server.Authentication;

/// <summary>
///     The bind credentials presented by an ESME during a <c>bind_*</c> operation.
/// </summary>
/// <param name="SystemId">The <c>system_id</c> field — the ESME's login / username.</param>
/// <param name="Password">The <c>password</c> field — secret or token presented by the ESME.</param>
/// <param name="SystemType">The optional <c>system_type</c> field categorising the ESME.</param>
/// <param name="RemoteAddress">The remote IP address of the connecting ESME, when available.</param>
public readonly record struct SmppCredentials(
    string SystemId,
    string Password,
    string? SystemType,
    string? RemoteAddress);
