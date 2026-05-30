using System.Collections.Concurrent;
using Elyfe.Smpp.Server.Bridge;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server;

/// <summary>
///     Tracks active <see cref="SmppServerSession" /> instances, enforces connection limits, routes mobile-originated
///     deliveries / receipts to the correct tenant session, and sweeps idle connections.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, SmppServerSession> _sessions = new();
    private readonly SmppServerOptions _options;
    private readonly ILogger<SessionManager> _logger;

    /// <summary>Creates a session manager bound to the supplied options.</summary>
    public SessionManager(SmppServerOptions options, ILogger<SessionManager> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>The number of currently tracked sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    ///     Attempts to register a newly accepted session. Returns <c>false</c> when the global session limit has been
    ///     reached, in which case the caller should close the connection.
    /// </summary>
    public bool TryRegister(SmppServerSession session)
    {
        if (_sessions.Count >= _options.MaxSessions)
        {
            _logger.LogWarning("Rejecting connection {SessionId}: max sessions ({Max}) reached",
                session.SessionId, _options.MaxSessions);
            return false;
        }

        return _sessions.TryAdd(session.SessionId, session);
    }

    /// <summary>Removes a session from the registry.</summary>
    public void Remove(SmppServerSession session) => _sessions.TryRemove(session.SessionId, out _);

    /// <summary>
    ///     Returns the number of bound sessions sharing the supplied <c>system_id</c>. Used to enforce the
    ///     per-system_id connection cap.
    /// </summary>
    public int CountBySystemId(string systemId)
        => _sessions.Values.Count(s =>
            string.Equals(s.SystemId, systemId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Delivers a mobile-originated message or delivery receipt to a bound receiver/transceiver session for the
    ///     target tenant. Returns <c>true</c> when at least one session accepted the delivery.
    /// </summary>
    public async Task<bool> DeliverToTenantAsync(SmppDeliverRequest request, CancellationToken cancellationToken)
    {
        var candidates = _sessions.Values
            .Where(s => string.Equals(s.TenantId, request.TenantId, StringComparison.Ordinal)
                        && s.Status is SmppSessionStatus.BoundRx or SmppSessionStatus.BoundTrx)
            .ToList();

        foreach (var session in candidates)
        {
            try
            {
                if (await session.DeliverAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Delivery to session {SessionId} failed", session.SessionId);
            }
        }

        _logger.LogWarning("No bound receiver session available for tenant {TenantId}", request.TenantId);
        return false;
    }

    /// <summary>
    ///     Closes sessions that have exceeded the configured idle timeout. Invoked periodically by the server.
    /// </summary>
    public async Task SweepIdleSessionsAsync()
    {
        if (_options.IdleTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - _options.IdleTimeout;
        foreach (var session in _sessions.Values)
        {
            if (session.LastActivityUtc < cutoff)
            {
                _logger.LogInformation("Closing idle session {SessionId} ({SystemId})",
                    session.SessionId, session.SystemId);
                Remove(session);
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Gracefully disposes all tracked sessions during shutdown.</summary>
    public async Task CloseAllAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}
