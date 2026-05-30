using Elyfe.Smpp.Server.Authentication;
using Elyfe.Smpp.Server.Bridge;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     The surface a <see cref="SmppServerSession" /> exposes to PDU handlers. Handlers are stateless and
///     operate against this context so they never touch socket or threading internals directly.
/// </summary>
public interface ISmppSessionContext
{
    /// <summary>A stable identifier for the session, used in logs.</summary>
    string SessionId { get; }

    /// <summary>The current session lifecycle state.</summary>
    SmppSessionStatus Status { get; }

    /// <summary>The bound <c>system_id</c>, or <c>null</c> before bind.</summary>
    string? SystemId { get; }

    /// <summary>The tenant resolved at bind time, or <c>null</c> before bind.</summary>
    string? TenantId { get; }

    /// <summary>The negotiated SMPP interface version (e.g. 0x34 or 0x50).</summary>
    byte InterfaceVersion { get; }

    /// <summary>The remote endpoint address of the connected ESME.</summary>
    string? RemoteAddress { get; }

    /// <summary>Server configuration.</summary>
    SmppServerOptions Options { get; }

    /// <summary>The SMPP byte encoding service shared with the PDU library.</summary>
    SmppEncodingService Encoding { get; }

    /// <summary>The authenticator used to validate bind credentials.</summary>
    IAuthenticator Authenticator { get; }

    /// <summary>The platform message bridge.</summary>
    IMessageHandler MessageHandler { get; }

    /// <summary>A logger scoped to the session.</summary>
    ILogger Logger { get; }

    /// <summary>Sends a PDU to the connected ESME.</summary>
    Task SendAsync(PDU pdu, CancellationToken cancellationToken);

    /// <summary>Sends raw, pre-serialised bytes to the connected ESME.</summary>
    Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken);

    /// <summary>
    ///     Transitions the session into a bound state after successful authentication.
    /// </summary>
    void MarkBound(SmppBindMode mode, string systemId, string tenantId, byte interfaceVersion);

    /// <summary>Begins the unbind / close sequence.</summary>
    void MarkUnbound();

    /// <summary>Allocates the next server-originated sequence number.</summary>
    uint NextSequenceNumber();

    /// <summary>
    ///     Records an inbound submit for rate limiting and returns <c>true</c> when the submit is within the
    ///     configured per-second budget.
    /// </summary>
    bool TryConsumeSubmitToken();
}
