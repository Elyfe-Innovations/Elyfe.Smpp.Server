using Elyfe.Smpp.Server.Bridge;

namespace Elyfe.Smpp.Server;

/// <summary>
///     The SMPP server: accepts ESME connections and exposes a delivery channel for pushing mobile-originated
///     messages and delivery receipts back to bound sessions.
/// </summary>
public interface ISmppServer
{
    /// <summary>Whether the TCP listener is currently accepting connections.</summary>
    bool IsListening { get; }

    /// <summary>The number of currently active sessions.</summary>
    int ActiveSessions { get; }

    /// <summary>Starts the accept loop and idle sweeper.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Stops accepting, unbinds bound sessions (waiting up to
    ///     <see cref="SmppServerOptions.ShutdownDrainTimeout" /> for in-flight exchanges) and closes all sessions.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Delivers a mobile-originated message or delivery receipt to a bound session for the target tenant.
    ///     Returns <c>true</c> only when the ESME acknowledged the <c>deliver_sm</c> with a successful
    ///     <c>deliver_sm_resp</c>; <c>false</c> signals the caller to retry later.
    /// </summary>
    Task<bool> DeliverAsync(SmppDeliverRequest request, CancellationToken cancellationToken);
}
