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
    private readonly object _gate = new();
    private int _reservedSlots;
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
    ///     Atomically reserves a connection slot against <see cref="SmppServerOptions.MaxSessions" /> for a newly
    ///     accepted connection. The slot is held from accept (before any TLS handshake) until
    ///     <see cref="ReleaseSlot" /> is called. Returns <c>false</c> when the global limit has been reached, in which
    ///     case the caller should close the connection.
    /// </summary>
    public bool TryReserveSlot()
    {
        lock (_gate)
        {
            if (_options.MaxSessions > 0 && _reservedSlots >= _options.MaxSessions)
            {
                _logger.LogWarning("Rejecting connection: max sessions ({Max}) reached", _options.MaxSessions);
                return false;
            }

            _reservedSlots++;
            return true;
        }
    }

    /// <summary>Releases a slot previously obtained from <see cref="TryReserveSlot" />.</summary>
    public void ReleaseSlot()
    {
        lock (_gate)
        {
            if (_reservedSlots > 0)
            {
                _reservedSlots--;
            }
        }
    }

    /// <summary>Adds a session to the registry. The caller must already hold a slot for it.</summary>
    public void Register(SmppServerSession session) => _sessions[session.SessionId] = session;

    /// <summary>Removes a session from the registry, releasing any <c>system_id</c> reservation it holds.</summary>
    public void Remove(SmppServerSession session) => _sessions.TryRemove(session.SessionId, out _);

    /// <summary>
    ///     Returns the number of registered sessions bound, or in the process of binding, under the supplied
    ///     <c>system_id</c>.
    /// </summary>
    public int CountBySystemId(string systemId)
        => _sessions.Values.Count(s =>
            string.Equals(s.ReservedSystemId, systemId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Atomically checks the per-<c>system_id</c> cap and, when there is room, reserves a place for
    ///     <paramref name="session" /> under <paramref name="systemId" />. The reservation is taken before
    ///     authentication so concurrent binds cannot all pass the check, and is dropped by
    ///     <see cref="ReleaseSystemId" /> or when the session is removed.
    /// </summary>
    public bool TryReserveSystemId(SmppServerSession session, string systemId)
    {
        lock (_gate)
        {
            if (_options.MaxSessionsPerSystemId > 0)
            {
                var others = _sessions.Values.Count(s =>
                    !ReferenceEquals(s, session)
                    && string.Equals(s.ReservedSystemId, systemId, StringComparison.OrdinalIgnoreCase));
                if (others >= _options.MaxSessionsPerSystemId)
                {
                    return false;
                }
            }

            session.ReservedSystemId = systemId;
            return true;
        }
    }

    /// <summary>Drops the <c>system_id</c> reservation held by a session whose bind did not complete.</summary>
    public void ReleaseSystemId(SmppServerSession session)
    {
        lock (_gate)
        {
            session.ReservedSystemId = null;
        }
    }

    /// <summary>
    ///     Delivers a mobile-originated message or delivery receipt to a bound receiver/transceiver session for the
    ///     target tenant. Returns <c>true</c> when the ESME on one of those sessions acknowledged the delivery.
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

    /// <summary>
    ///     Sends <c>unbind</c> to every bound session and waits, up to <paramref name="timeout" />, for each to finish
    ///     its in-flight exchange and acknowledge. Sessions still open afterwards are left for
    ///     <see cref="CloseAllAsync" />.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sessions = _sessions.Values.ToList();
        if (sessions.Count == 0 || timeout <= TimeSpan.Zero)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await Task.WhenAll(sessions.Select(s => s.UnbindAsync(cts.Token))).ConfigureAwait(false);
    }

    /// <summary>Disposes all tracked sessions during shutdown.</summary>
    public async Task CloseAllAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}
