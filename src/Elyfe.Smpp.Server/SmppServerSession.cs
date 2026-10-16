using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Elyfe.Smpp.Server.Authentication;
using Elyfe.Smpp.Server.Bridge;
using Elyfe.Smpp.Server.Handlers;
using Elyfe.Smpp.Server.Networking;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server;

/// <summary>
///     Represents a single connected ESME session. Owns the transport stream, runs the PDU read loop, dispatches
///     requests to the stateless handlers and tracks the bind / lifecycle state. One instance per TCP connection.
/// </summary>
public sealed class SmppServerSession : ISmppSessionContext, IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly Stream _stream;
    private readonly SmppPduChannel _channel;
    private readonly object _rateLock = new();
    private readonly SessionManager? _sessionManager;

    // Server-originated deliver_sm PDUs awaiting deliver_sm_resp, keyed by sequence number. A null result means the
    // session closed before the ESME answered.
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<SmppErrorCode?>> _pendingDeliveries = new();
    private readonly SemaphoreSlim? _deliverWindow;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _sequenceNumber;
    private SmppBindMode _bindMode = SmppBindMode.None;
    private CancellationTokenSource? _bindTimeoutCts;
    private volatile bool _unbindRequested;
    private int _disposed;

    // Token bucket for submit rate limiting.
    private int _submitTokens;
    private long _windowTicks;

    /// <summary>
    ///     Creates a session over an accepted connection. When a <paramref name="sessionManager" /> is supplied it
    ///     enforces the per-<c>system_id</c> connection cap at bind time.
    /// </summary>
    public SmppServerSession(
        Socket socket,
        Stream stream,
        SmppServerOptions options,
        SmppEncodingService encoding,
        IAuthenticator authenticator,
        IMessageHandler messageHandler,
        ILogger logger,
        SmppServerMetrics? metrics = null,
        SessionManager? sessionManager = null)
    {
        _socket = socket;
        _stream = stream;
        Options = options;
        Encoding = encoding;
        Authenticator = authenticator;
        MessageHandler = messageHandler;
        Logger = logger;
        Metrics = metrics ?? new SmppServerMetrics();
        _sessionManager = sessionManager;
        _deliverWindow = options.WindowSize > 0 ? new SemaphoreSlim(options.WindowSize, options.WindowSize) : null;
        _channel = new SmppPduChannel(stream, encoding);

        SessionId = Guid.NewGuid().ToString("N");
        RemoteAddress = socket.RemoteEndPoint?.ToString();
        Status = SmppSessionStatus.Open;
        LastActivityUtc = DateTimeOffset.UtcNow;
        _submitTokens = options.MaxSubmitsPerSecond;
        _windowTicks = DateTimeOffset.UtcNow.Ticks;
    }

    /// <summary>The UTC timestamp of the last inbound PDU, used for idle-timeout enforcement.</summary>
    public DateTimeOffset LastActivityUtc { get; private set; }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public SmppSessionStatus Status { get; private set; }

    /// <inheritdoc />
    public string? SystemId { get; private set; }

    /// <summary>
    ///     The <c>system_id</c> this session counts against for the per-<c>system_id</c> cap. Set from the start of a
    ///     bind attempt; owned by <see cref="SessionManager" />.
    /// </summary>
    internal string? ReservedSystemId { get; set; }

    /// <inheritdoc />
    public string? TenantId { get; private set; }

    /// <inheritdoc />
    public byte InterfaceVersion { get; private set; }

    /// <inheritdoc />
    public string? RemoteAddress { get; }

    /// <inheritdoc />
    public SmppServerOptions Options { get; }

    /// <inheritdoc />
    public SmppEncodingService Encoding { get; }

    /// <inheritdoc />
    public IAuthenticator Authenticator { get; }

    /// <inheritdoc />
    public IMessageHandler MessageHandler { get; }

    /// <inheritdoc />
    public ILogger Logger { get; }

    /// <inheritdoc />
    public SmppServerMetrics Metrics { get; }

    /// <summary>
    ///     Runs the session: enforces the bind timeout, then reads PDUs until the peer unbinds, the connection closes,
    ///     or the host shuts down. Responses are handled as they are read, while requests are handed to a worker that
    ///     processes them in arrival order, so a slow request handler never stops the session from seeing the
    ///     <c>deliver_sm_resp</c> it (or another caller) is waiting for.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _bindTimeoutCts = new CancellationTokenSource(Options.BindTimeout);

        // Cancelled by the request worker once it has ended the session (an unbind or a failed write).
        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _bindTimeoutCts.Token, stop.Token);

        // Bounded so an ESME that pipelines beyond the window is held back by TCP rather than buffered without limit.
        var requests = Options.WindowSize > 0
            ? Channel.CreateBounded<SmppFrame>(
                new BoundedChannelOptions(Options.WindowSize) { SingleReader = true, SingleWriter = true })
            : Channel.CreateUnbounded<SmppFrame>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var worker = ProcessRequestsAsync(requests.Reader, stop, cancellationToken);

        try
        {
            while (!linked.IsCancellationRequested && Status is not SmppSessionStatus.Closed)
            {
                SmppFrame? frame;
                try
                {
                    frame = await _channel.ReadAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_bindTimeoutCts.IsCancellationRequested && SystemId is null)
                {
                    Logger.LogWarning("Session {SessionId} closed: bind timeout elapsed", SessionId);
                    break;
                }
                catch (InvalidDataException ex)
                {
                    Logger.LogWarning(ex, "Session {SessionId} closed: malformed PDU", SessionId);

                    // SMPP v3.4 §4.3: an invalid command_length must be answered with a generic_nack. The stream can
                    // no longer be framed, so the sequence number is NULL and the connection is then closed.
                    var lengthNack = GenericNackHandler.Build(Encoding, 0, SmppErrorCode.ESME_RINVCMDLEN);
                    await SendAsync(lengthNack, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (frame is null)
                {
                    break; // Peer closed the connection.
                }

                LastActivityUtc = DateTimeOffset.UtcNow;

                if (IsResponse(frame.Value.Header))
                {
                    await DispatchAsync(frame.Value, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await requests.Writer.WriteAsync(frame.Value, linked.Token).ConfigureAwait(false);
                }

                if (_unbindRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown or connection cancellation; fall through to cleanup.
        }
        catch (IOException ex)
        {
            Logger.LogDebug(ex, "Session {SessionId} I/O ended", SessionId);
        }
        finally
        {
            Status = SmppSessionStatus.Closed;
            FailPendingDeliveries();

            // The request already being handled finishes and is answered; requests still queued are dropped.
            requests.Writer.TryComplete();
            await worker.ConfigureAwait(false);

            Status = SmppSessionStatus.Closed;
            _closed.TrySetResult();
        }
    }

    private static bool IsResponse(PDUHeader header) => ((uint)header.CommandType & 0x80000000) != 0;

    /// <summary>
    ///     Handles queued requests one at a time, in the order they were read. Never throws: any failure ends the
    ///     session by cancelling <paramref name="stop" />.
    /// </summary>
    private async Task ProcessRequestsAsync(
        ChannelReader<SmppFrame> requests, CancellationTokenSource stop, CancellationToken cancellationToken)
    {
        try
        {
            while (await requests.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (requests.TryRead(out var frame))
                {
                    if (Status is SmppSessionStatus.Closed)
                    {
                        return;
                    }

                    await DispatchAsync(frame, cancellationToken).ConfigureAwait(false);

                    if (_unbindRequested)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown; the read loop is ending too.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Logger.LogDebug(ex, "Session {SessionId} I/O ended while answering a request", SessionId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Session {SessionId} closed: request handling failed", SessionId);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(SmppFrame frame, CancellationToken cancellationToken)
    {
        if (frame.ParseError is { } parseError)
        {
            await HandleMalformedAsync(frame.Header, parseError, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (frame.Pdu is null)
        {
            // Unrecognised command id (e.g. SMPP v5.0 broadcast operations absent from the PDU library).
            var nack = GenericNackHandler.Build(Encoding, frame.Header.SequenceNumber, SmppErrorCode.ESME_RINVCMDID);
            await SendAsync(nack, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (frame.Pdu)
        {
            case BindRequest bind:
                await BindRequestHandler.HandleAsync(this, bind, cancellationToken).ConfigureAwait(false);
                break;
            case SubmitSm submit:
                await SubmitSmHandler.HandleAsync(this, submit, cancellationToken).ConfigureAwait(false);
                break;
            case DataSm dataSm:
                await DataSmHandler.HandleAsync(this, dataSm, cancellationToken).ConfigureAwait(false);
                break;
            case QuerySm query:
                await QuerySmHandler.HandleAsync(this, query, cancellationToken).ConfigureAwait(false);
                break;
            case CancelSm cancel:
                await CancelSmHandler.HandleAsync(this, cancel, cancellationToken).ConfigureAwait(false);
                break;
            case ReplaceSm replace:
                await ReplaceSmHandler.HandleAsync(this, replace, cancellationToken).ConfigureAwait(false);
                break;
            case EnquireLink enquire:
                await EnquireLinkHandler.HandleAsync(this, enquire, cancellationToken).ConfigureAwait(false);
                break;
            case Unbind unbind:
                await UnbindHandler.HandleAsync(this, unbind, cancellationToken).ConfigureAwait(false);
                break;
            case DeliverSmResp or GenericNack:
                // The ESME's answer to a server-originated deliver_sm (a generic_nack carries its sequence number).
                CompletePendingDelivery(frame.Header);
                break;
            case UnbindResp:
                // The ESME acknowledged a server-initiated unbind; the exchange is drained.
                _unbindRequested = true;
                break;
            case ResponsePDU:
                // Other responses (e.g. enquire_link_resp) need no reply; LastActivityUtc is already refreshed.
                break;
            default:
                var nack = GenericNackHandler.Build(Encoding, frame.Header.SequenceNumber, SmppErrorCode.ESME_RINVCMDID);
                await SendAsync(nack, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    ///     Answers a recognised PDU whose body could not be parsed. The frame was read in full, so the session stays
    ///     open: a request gets its own response type with the error status (SMPP v5.0 §2.8.2), and a response is
    ///     acted on from its header alone.
    /// </summary>
    private async Task HandleMalformedAsync(PDUHeader header, SmppErrorCode parseError, CancellationToken cancellationToken)
    {
        const uint responseBit = 0x80000000;
        const uint headerLength = 16;
        var commandId = (uint)header.CommandType;

        if ((commandId & responseBit) != 0)
        {
            Logger.LogDebug(
                "Session {SessionId} received {Command} with an unparseable body ({Error}); using its header only",
                SessionId, header.CommandType, parseError);

            // command_status is authoritative, e.g. a v5.0 deliver_sm_resp carrying TLVs the PDU library rejects.
            switch (header.CommandType)
            {
                case CommandType.DeliverSmResp or CommandType.GenericNack:
                    CompletePendingDelivery(header);
                    break;
                case CommandType.UnBindResp:
                    _unbindRequested = true;
                    break;
            }

            return;
        }

        Logger.LogWarning(
            "Session {SessionId} rejected malformed {Command} (sequence {Sequence}) with {Error}",
            SessionId, header.CommandType, header.SequenceNumber, parseError);

        if (header.CommandType is CommandType.OutBind or CommandType.AlertNotification)
        {
            // These operations have no response PDU of their own.
            var nack = GenericNackHandler.Build(Encoding, header.SequenceNumber, parseError);
            await SendAsync(nack, cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = new PDUHeader(
            (CommandType)(commandId | responseBit), header.SequenceNumber, parseError, headerLength);
        await SendRawAsync(response.GetBytes(Encoding), cancellationToken).ConfigureAwait(false);
    }

    private void CompletePendingDelivery(PDUHeader header)
    {
        if (_pendingDeliveries.TryRemove(header.SequenceNumber, out var pending))
        {
            pending.TrySetResult(header.ErrorCode);
        }
    }

    /// <summary>
    ///     Pushes a mobile-originated message or delivery receipt to this session when it is bound as a receiver or
    ///     transceiver, and waits for the ESME's <c>deliver_sm_resp</c>. Returns <c>true</c> only when the ESME
    ///     acknowledged the delivery with <c>ESME_ROK</c>; <c>false</c> when the session cannot accept deliveries, the
    ///     ESME rejected the message, the session closed, or no response (or free window slot) arrived within
    ///     <see cref="SmppServerOptions.DeliverResponseTimeout" />.
    /// </summary>
    public async Task<bool> DeliverAsync(SmppDeliverRequest request, CancellationToken cancellationToken)
    {
        if (!CanReceiveDeliveries)
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Options.DeliverResponseTimeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(Options.DeliverResponseTimeout);
        }

        var windowHeld = false;
        uint? sequenceNumber = null;
        try
        {
            if (_deliverWindow is not null)
            {
                await _deliverWindow.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                windowHeld = true;
            }

            sequenceNumber = NextSequenceNumber();
            var pending = new TaskCompletionSource<SmppErrorCode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingDeliveries[sequenceNumber.Value] = pending;

            // Re-check after registering so a session that closed meanwhile cannot strand the waiter.
            if (!CanReceiveDeliveries)
            {
                return false;
            }

            var deliver = DeliverSmDispatcher.Build(request, Encoding, sequenceNumber.Value, InterfaceVersion);
            await SendAsync(deliver, timeoutCts.Token).ConfigureAwait(false);

            var status = await pending.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (status == SmppErrorCode.ESME_ROK)
            {
                return true;
            }

            Logger.LogWarning(
                "deliver_sm {SequenceNumber} on session {SessionId} was not acknowledged: {Status}",
                sequenceNumber, SessionId, status?.ToString() ?? "session closed");
            Metrics.RecordError(status is null ? "deliver_session_closed" : "deliver_rejected");
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(
                "deliver_sm on session {SessionId} timed out after {Timeout} waiting for {Awaited}",
                SessionId, Options.DeliverResponseTimeout, sequenceNumber is null ? "a window slot" : "deliver_sm_resp");
            Metrics.RecordError("deliver_timeout");
            return false;
        }
        finally
        {
            if (sequenceNumber is not null)
            {
                _pendingDeliveries.TryRemove(sequenceNumber.Value, out _);
            }

            if (windowHeld)
            {
                _deliverWindow!.Release();
            }
        }
    }

    /// <summary>
    ///     Begins a server-initiated unbind for graceful shutdown: stops accepting new work, sends <c>unbind</c> and
    ///     waits until the session has seen <c>unbind_resp</c> (or the peer closed) and answered the request it was
    ///     handling. Returns early, leaving the session open, if <paramref name="cancellationToken" /> fires first.
    /// </summary>
    public async Task UnbindAsync(CancellationToken cancellationToken)
    {
        if (Status is not (SmppSessionStatus.BoundTx or SmppSessionStatus.BoundRx or SmppSessionStatus.BoundTrx))
        {
            return;
        }

        Status = SmppSessionStatus.Unbound;
        try
        {
            var unbind = PDU.CreatePDU(new PDUHeader(CommandType.UnBind, NextSequenceNumber()), Encoding);
            await SendAsync(unbind, cancellationToken).ConfigureAwait(false);
            await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("Session {SessionId} ({SystemId}) did not acknowledge unbind in time", SessionId, SystemId);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Logger.LogDebug(ex, "Session {SessionId} closed while unbinding", SessionId);
        }
    }

    private bool CanReceiveDeliveries => Status is SmppSessionStatus.BoundRx or SmppSessionStatus.BoundTrx;

    private void FailPendingDeliveries()
    {
        foreach (var sequenceNumber in _pendingDeliveries.Keys)
        {
            if (_pendingDeliveries.TryRemove(sequenceNumber, out var pending))
            {
                pending.TrySetResult(null);
            }
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(PDU pdu, CancellationToken cancellationToken)
        => await _channel.WriteAsync(pdu, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken)
        => await _channel.WriteRawAsync(bytes, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void MarkBound(SmppBindMode mode, string systemId, string tenantId, byte interfaceVersion)
    {
        _bindMode = mode;
        SystemId = systemId;
        TenantId = tenantId;
        InterfaceVersion = interfaceVersion;
        Status = mode switch
        {
            SmppBindMode.Transmitter => SmppSessionStatus.BoundTx,
            SmppBindMode.Receiver => SmppSessionStatus.BoundRx,
            _ => SmppSessionStatus.BoundTrx
        };

        // Stop the bind-timeout clock now that the session is bound.
        _bindTimeoutCts?.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public bool TryReserveSystemId(string systemId)
        => _sessionManager is null || _sessionManager.TryReserveSystemId(this, systemId);

    /// <inheritdoc />
    public void ReleaseSystemId() => _sessionManager?.ReleaseSystemId(this);

    /// <inheritdoc />
    public void MarkUnbound()
    {
        Status = SmppSessionStatus.Unbound;
        _unbindRequested = true;
    }

    /// <inheritdoc />
    public uint NextSequenceNumber()
    {
        var next = Interlocked.Increment(ref _sequenceNumber);
        // SMPP sequence numbers occupy 0x00000001..0x7FFFFFFF; wrap within that range.
        var wrapped = (uint)((next - 1) % 0x7FFFFFFF) + 1;
        return wrapped;
    }

    /// <inheritdoc />
    public bool TryConsumeSubmitToken()
    {
        var max = Options.MaxSubmitsPerSecond;
        if (max <= 0)
        {
            return true; // Throttling disabled.
        }

        lock (_rateLock)
        {
            var now = DateTimeOffset.UtcNow.Ticks;
            if (now - _windowTicks >= TimeSpan.TicksPerSecond)
            {
                _windowTicks = now;
                _submitTokens = max;
            }

            if (_submitTokens <= 0)
            {
                return false;
            }

            _submitTokens--;
            return true;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Status = SmppSessionStatus.Closed;
        FailPendingDeliveries();
        _bindTimeoutCts?.Dispose();

        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error disposing stream for session {SessionId}", SessionId);
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Error disposing socket for session {SessionId}", SessionId);
        }
    }
}
