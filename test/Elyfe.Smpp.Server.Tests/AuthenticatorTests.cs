using Elyfe.Smpp.Server.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elyfe.Smpp.Server.Tests;

/// <summary>
///     Tests for the static credential authenticators (config- and file-based).
/// </summary>
public class AuthenticatorTests
{
    private static SmppCredentials Credentials(string systemId, string password)
        => new(systemId, password, null, "127.0.0.1");

    [Fact]
    public async Task ConfigAuthenticator_AcceptsValidPlaintextPassword()
    {
        var options = new SmppAuthenticationOptions
        {
            Accounts =
            [
                new SmppAccount { SystemId = "esme01", Password = "secret", TenantId = "tenant-a" }
            ]
        };
        var authenticator = new ConfigBasedAuthenticator(new StaticOptionsMonitor<SmppAuthenticationOptions>(options));

        var result = await authenticator.AuthenticateAsync(Credentials("esme01", "secret"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("tenant-a", result.TenantId);
    }

    [Fact]
    public async Task ConfigAuthenticator_AcceptsValidHashedPassword()
    {
        var hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("hunter2")));
        var options = new SmppAuthenticationOptions
        {
            Accounts =
            [
                new SmppAccount { SystemId = "esme02", PasswordSha256 = hash, TenantId = "tenant-b" }
            ]
        };
        var authenticator = new ConfigBasedAuthenticator(new StaticOptionsMonitor<SmppAuthenticationOptions>(options));

        var result = await authenticator.AuthenticateAsync(Credentials("esme02", "hunter2"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("tenant-b", result.TenantId);
    }

    [Fact]
    public async Task ConfigAuthenticator_RejectsWrongPassword()
    {
        var options = new SmppAuthenticationOptions
        {
            Accounts = [new SmppAccount { SystemId = "esme01", Password = "secret", TenantId = "tenant-a" }]
        };
        var authenticator = new ConfigBasedAuthenticator(new StaticOptionsMonitor<SmppAuthenticationOptions>(options));

        var result = await authenticator.AuthenticateAsync(Credentials("esme01", "nope"), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ConfigAuthenticator_RejectsDisabledAccount()
    {
        var options = new SmppAuthenticationOptions
        {
            Accounts =
            [
                new SmppAccount { SystemId = "esme01", Password = "secret", TenantId = "tenant-a", Enabled = false }
            ]
        };
        var authenticator = new ConfigBasedAuthenticator(new StaticOptionsMonitor<SmppAuthenticationOptions>(options));

        var result = await authenticator.AuthenticateAsync(Credentials("esme01", "secret"), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ConfigAuthenticator_RejectsUnknownSystemId()
    {
        var options = new SmppAuthenticationOptions { Accounts = [] };
        var authenticator = new ConfigBasedAuthenticator(new StaticOptionsMonitor<SmppAuthenticationOptions>(options));

        var result = await authenticator.AuthenticateAsync(Credentials("ghost", "x"), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task FileAuthenticator_LoadsAccountsAndAuthenticates()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path,
            """[{"systemId":"esme-file","password":"filepass","tenantId":"tenant-f"}]""");
        try
        {
            var authenticator = new FileBasedAuthenticator(
                new FileAuthenticatorOptions { FilePath = path },
                NullLogger<FileBasedAuthenticator>.Instance);

            var ok = await authenticator.AuthenticateAsync(Credentials("esme-file", "filepass"), CancellationToken.None);
            var bad = await authenticator.AuthenticateAsync(Credentials("esme-file", "wrong"), CancellationToken.None);

            Assert.True(ok.Succeeded);
            Assert.Equal("tenant-f", ok.TenantId);
            Assert.False(bad.Succeeded);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>A minimal <see cref="IOptionsMonitor{T}" /> returning a fixed value for tests.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
