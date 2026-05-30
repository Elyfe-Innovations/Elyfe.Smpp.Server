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

    /// <summary>Stops accepting and gracefully closes all sessions.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Delivers a mobile-originated message or delivery receipt to a bound session for the target tenant.
    ///     Returns <c>true</c> when a session accepted the delivery.
    /// </summary>
    Task<bool> DeliverAsync(SmppDeliverRequest request, CancellationToken cancellationToken);
}
