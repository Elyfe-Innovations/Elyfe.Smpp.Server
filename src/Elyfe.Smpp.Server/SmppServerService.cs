using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server;

/// <summary>
///     Hosts the <see cref="ISmppServer" /> within the generic host lifetime, starting the accept loop on application
///     start and gracefully draining sessions on shutdown.
/// </summary>
public sealed class SmppServerService : IHostedService
{
    private readonly ISmppServer _server;
    private readonly ILogger<SmppServerService> _logger;

    /// <summary>Creates the hosted service over the supplied server.</summary>
    public SmppServerService(ISmppServer server, ILogger<SmppServerService> logger)
    {
        _server = server;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SMPP server hosted service");
        await _server.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SMPP server hosted service");
        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
