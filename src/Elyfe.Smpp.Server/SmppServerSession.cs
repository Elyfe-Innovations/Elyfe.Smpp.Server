using System.Net.Sockets;
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
    private readonly Func<string, bool>? _canBindSystemId;

    private long _sequenceNumber;
    private SmppBindMode _bindMode = SmppBindMode.None;
    private CancellationTokenSource? _bindTimeoutCts;
    private volatile bool _unbindRequested;
    private int _disposed;

    // Token bucket for submit rate limiting.
    private int _submitTokens;
    private long _windowTicks;

    /// <summary>
    ///     Creates a session over an accepted connection.
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
        Func<string, bool>? canBindSystemId = null)
    {
        _socket = socket;
        _stream = stream;
        Options = options;
        Encoding = encoding;
        Authenticator = authenticator;
        MessageHandler = messageHandler;
        Logger = logger;
        Metrics = metrics ?? new SmppServerMetrics();
        _canBindSystemId = canBindSystemId;
        _channel = new SmppPduChannel(stream, encoding);

        SessionId = Guid.NewGuid().ToString("N");
        RemoteAddress = socket.RemoteEndPoint?.ToString();
        Status = SmppSessionStatus.Open;
        LastActivityUtc = DateTimeOffset.UtcNow;
        _submitTokens = Math.Max(1, options.MaxSubmitsPerSecond);
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
    ///     Runs the session: enforces the bind timeout, then reads and dispatches PDUs until the peer unbinds, the
    ///     connection closes, or the host shuts down.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _bindTimeoutCts = new CancellationTokenSource(Options.BindTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _bindTimeoutCts.Token);

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
                    break;
                }

                if (frame is null)
                {
                    break; // Peer closed the connection.
                }

                LastActivityUtc = DateTimeOffset.UtcNow;
                await DispatchAsync(frame.Value, cancellationToken).ConfigureAwait(false);

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
        }
    }

    private async Task DispatchAsync(SmppFrame frame, CancellationToken cancellationToken)
    {
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
            default:
                var nack = GenericNackHandler.Build(Encoding, frame.Header.SequenceNumber, SmppErrorCode.ESME_RINVCMDID);
                await SendAsync(nack, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    ///     Pushes a mobile-originated message or delivery receipt to this session when it is bound as a receiver or
    ///     transceiver. Returns <c>false</c> when the session cannot currently accept deliveries.
    /// </summary>
    public async Task<bool> DeliverAsync(SmppDeliverRequest request, CancellationToken cancellationToken)
    {
        if (Status is not (SmppSessionStatus.BoundRx or SmppSessionStatus.BoundTrx))
        {
            return false;
        }

        var deliver = DeliverSmDispatcher.Build(request, Encoding, NextSequenceNumber());
        await SendAsync(deliver, cancellationToken).ConfigureAwait(false);
        return true;
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
    public bool CanBindSystemId(string systemId)
        => _canBindSystemId is null || _canBindSystemId(systemId);

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
        var max = Math.Max(1, Options.MaxSubmitsPerSecond);
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
