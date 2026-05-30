using Elyfe.Smpp.Server.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Elyfe.Smpp.Server;

/// <summary>
///     Dependency-injection helpers for registering the SMPP server and its pluggable authenticators.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the SMPP server, session manager and hosted service. The caller is responsible for registering an
    ///     <see cref="Bridge.IMessageHandler" /> and an <see cref="IAuthenticator" /> (use one of the
    ///     <c>AddSmpp*Authentication</c> helpers).
    /// </summary>
    public static IServiceCollection AddSmppServer(
        this IServiceCollection services,
        Action<SmppServerOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<SmppServerOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<SmppServerOptions>>().Value);
        services.TryAddSingleton<SmppServerMetrics>();
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<ISmppServer, SmppServer>();
        services.AddHostedService<SmppServerService>();

        return services;
    }

    /// <summary>
    ///     Binds <see cref="SmppServerOptions" /> from the supplied configuration section in addition to registering the
    ///     server.
    /// </summary>
    public static IServiceCollection AddSmppServer(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<SmppServerOptions>? configure = null)
    {
        services.Configure<SmppServerOptions>(configuration.GetSection(SmppServerOptions.SectionName));
        return services.AddSmppServer(configure);
    }

    /// <summary>
    ///     Registers the configuration-based authenticator, binding accounts from
    ///     <see cref="SmppAuthenticationOptions.SectionName" />.
    /// </summary>
    public static IServiceCollection AddSmppConfigAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SmppAuthenticationOptions>(
            configuration.GetSection(SmppAuthenticationOptions.SectionName));
        services.TryAddSingleton<IAuthenticator, ConfigBasedAuthenticator>();
        return services;
    }

    /// <summary>
    ///     Registers the configuration-based authenticator with accounts supplied in code.
    /// </summary>
    public static IServiceCollection AddSmppConfigAuthentication(
        this IServiceCollection services,
        Action<SmppAuthenticationOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<IAuthenticator, ConfigBasedAuthenticator>();
        return services;
    }

    /// <summary>
    ///     Registers the file-based authenticator that reads accounts from a JSON file at the supplied path.
    /// </summary>
    public static IServiceCollection AddSmppFileAuthentication(
        this IServiceCollection services,
        string filePath)
    {
        services.TryAddSingleton<IAuthenticator>(sp => new FileBasedAuthenticator(
            new FileAuthenticatorOptions { FilePath = filePath },
            sp.GetRequiredService<ILogger<FileBasedAuthenticator>>()));
        return services;
    }

    /// <summary>
    ///     Adds the SMPP server health check to a health-checks builder. Reports healthy while the listener is
    ///     accepting connections.
    /// </summary>
    public static IHealthChecksBuilder AddSmppServerHealthCheck(
        this IHealthChecksBuilder builder,
        string name = "smpp-server",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null)
    {
        builder.Add(new HealthCheckRegistration(
            name,
            sp => new SmppServerHealthCheck(sp.GetRequiredService<ISmppServer>()),
            failureStatus,
            tags));
        return builder;
    }
}
