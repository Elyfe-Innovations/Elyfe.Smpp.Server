using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Elyfe.Smpp.Server.Authentication;
using Elyfe.Smpp.Server.Bridge;
using Elyfe.Smpp.Server.Networking;
using JamaaTech.Smpp.Net.Lib;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server;

/// <summary>
///     Default <see cref="ISmppServer" /> implementation. Runs a TCP (optionally TLS) accept loop, spins up a
///     <see cref="SmppServerSession" /> per connection and periodically sweeps idle sessions.
/// </summary>
public sealed class SmppServer : ISmppServer, IAsyncDisposable
{
    private readonly SmppServerOptions _options;
    private readonly SessionManager _sessionManager;
    private readonly IAuthenticator _authenticator;
    private readonly IMessageHandler _messageHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SmppServer> _logger;
    private readonly SmppServerMetrics _metrics;
    private readonly SmppEncodingService _encoding = new();

    private TcpIpListener? _listener;
    private CancellationTokenSource? _stoppingCts;
    private Task? _acceptLoop;
    private Task? _idleSweepLoop;
    private X509Certificate2? _tlsCertificate;

    /// <summary>Creates a server with the supplied dependencies.</summary>
    public SmppServer(
        SmppServerOptions options,
        SessionManager sessionManager,
        IAuthenticator authenticator,
        IMessageHandler messageHandler,
        ILoggerFactory loggerFactory,
        SmppServerMetrics? metrics = null)
    {
        _options = options;
        _sessionManager = sessionManager;
        _authenticator = authenticator;
        _messageHandler = messageHandler;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SmppServer>();
        _metrics = metrics ?? new SmppServerMetrics();
        _metrics.TrackActiveSessions(() => _sessionManager.Count);
    }

    /// <summary>Whether the TCP listener is currently accepting connections.</summary>
    public bool IsListening { get; private set; }

    /// <summary>The number of currently active sessions.</summary>
    public int ActiveSessions => _sessionManager.Count;


    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Tls.Enabled)
        {
            if (string.IsNullOrWhiteSpace(_options.Tls.CertificatePath))
            {
                throw new InvalidOperationException("TLS is enabled but no certificate path is configured.");
            }

            _tlsCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                _options.Tls.CertificatePath,
                _options.Tls.CertificatePassword);        }

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpIpListener(_options.BindAddress, _options.Port);
        _listener.Start();

        _logger.LogInformation("SMPP server listening on {EndPoint} (TLS={Tls})",
            _listener.EndPoint, _options.Tls.Enabled);

        IsListening = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stoppingCts.Token), CancellationToken.None);
        _idleSweepLoop = Task.Run(() => IdleSweepLoopAsync(_stoppingCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SMPP server");
        IsListening = false;

        if (_stoppingCts is not null)
        {
            await _stoppingCts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Stop();

        if (_acceptLoop is not null)
        {
            await SafeAwaitAsync(_acceptLoop).ConfigureAwait(false);
        }

        if (_idleSweepLoop is not null)
        {
            await SafeAwaitAsync(_idleSweepLoop).ConfigureAwait(false);
        }

        await _sessionManager.CloseAllAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> DeliverAsync(SmppDeliverRequest request, CancellationToken cancellationToken)
        => _sessionManager.DeliverToTenantAsync(request, cancellationToken);

    /// <summary>
    ///     Per-<c>system_id</c> connection-cap predicate passed to each session. Returns <c>true</c> when another
    ///     session may bind under <paramref name="systemId" /> without exceeding <see cref="SmppServerOptions.MaxSessionsPerSystemId" />.
    /// </summary>
    private bool CanBindSystemId(string systemId)
        => _options.MaxSessionsPerSystemId <= 0
           || _sessionManager.CountBySystemId(systemId) < _options.MaxSessionsPerSystemId;

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener!.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Accept failed");
                continue;
            }

            _ = Task.Run(() => HandleConnectionAsync(socket, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        SmppServerSession? session = null;
        try
        {
            var stream = await CreateStreamAsync(socket, cancellationToken).ConfigureAwait(false);
            session = new SmppServerSession(
                socket,
                stream,
                _options,
                _encoding,
                _authenticator,
                _messageHandler,
                _loggerFactory.CreateLogger<SmppServerSession>(),
                _metrics,
                CanBindSystemId);

            if (!_sessionManager.TryRegister(session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection handling error");
        }
        finally
        {
            if (session is not null)
            {
                _sessionManager.Remove(session);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                socket.Dispose();
            }
        }
    }

    private async Task<Stream> CreateStreamAsync(Socket socket, CancellationToken cancellationToken)
    {
        Stream stream = new NetworkStream(socket, ownsSocket: false);
        if (!_options.Tls.Enabled || _tlsCertificate is null)
        {
            return stream;
        }

        var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
        await sslStream.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = _tlsCertificate,
                ClientCertificateRequired = false
            },
            cancellationToken).ConfigureAwait(false);
        return sslStream;
    }

    private async Task IdleSweepLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _options.EnquireLinkInterval > TimeSpan.Zero
            ? _options.EnquireLinkInterval
            : TimeSpan.FromSeconds(60);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await _sessionManager.SweepIdleSessionsAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background loop ended with error");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stoppingCts?.Dispose();
        _listener?.Dispose();
        _tlsCertificate?.Dispose();
    }
}
