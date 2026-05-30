using System.Net;
using System.Net.Sockets;

namespace Elyfe.Smpp.Server.Networking;

/// <summary>
///     A minimal server-side TCP accept loop. Complements the client-oriented
///     <c>JamaaTech.Smpp.Net.Lib.Networking.TcpIpSession</c> by providing the listen / accept path that the
///     original library lacks.
/// </summary>
public sealed class TcpIpListener : IDisposable
{
    private readonly IPEndPoint _endPoint;
    private TcpListener? _listener;

    /// <summary>
    ///     Creates a listener bound to the supplied address and port.
    /// </summary>
    /// <param name="bindAddress">The local IP address to bind to (e.g. <c>0.0.0.0</c>).</param>
    /// <param name="port">The TCP port to listen on.</param>
    public TcpIpListener(string bindAddress, int port)
    {
        var address = string.IsNullOrWhiteSpace(bindAddress) ? IPAddress.Any : IPAddress.Parse(bindAddress);
        _endPoint = new IPEndPoint(address, port);
    }

    /// <summary>
    ///     The local endpoint the listener is bound to.
    /// </summary>
    public IPEndPoint EndPoint => _endPoint;

    /// <summary>
    ///     Starts listening for incoming connections.
    /// </summary>
    public void Start()
    {
        _listener = new TcpListener(_endPoint);
        _listener.Start();
    }

    /// <summary>
    ///     Awaits the next inbound connection.
    /// </summary>
    /// <param name="cancellationToken">A token used to stop accepting.</param>
    /// <returns>The accepted <see cref="Socket" />, configured for low-latency SMPP traffic.</returns>
    public async Task<Socket> AcceptAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            throw new InvalidOperationException("Listener has not been started.");
        }

        var socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        socket.NoDelay = true; // Disable Nagle for SMPP request/response latency.
        return socket;
    }

    /// <summary>
    ///     Stops listening. Existing accepted sockets are unaffected.
    /// </summary>
    public void Stop() => _listener?.Stop();

    /// <inheritdoc />
    public void Dispose() => Stop();
}
