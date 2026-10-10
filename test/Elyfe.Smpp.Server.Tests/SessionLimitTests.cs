using System.Net.Sockets;
using Elyfe.Smpp.Server.Authentication;
using Elyfe.Smpp.Server.Bridge;
using Elyfe.Smpp.Server.Handlers;
using Elyfe.Smpp.Server.Networking;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elyfe.Smpp.Server.Tests;

/// <summary>
///     Tests for the session/system_id caps, submit throttling and <c>deliver_sm</c> construction.
/// </summary>
public class SessionLimitTests
{
    private static readonly SmppEncodingService Encoding = new();

    private static SessionManager Manager(SmppServerOptions options)
        => new(options, NullLogger<SessionManager>.Instance);

    private static SmppServerSession Session(SmppServerOptions options, SessionManager? manager = null)
    {
        var authenticator = new ConfigBasedAuthenticator(
            new StaticOptionsMonitor<SmppAuthenticationOptions>(new SmppAuthenticationOptions()));
        var session = new SmppServerSession(
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
            new MemoryStream(),
            options,
            Encoding,
            authenticator,
            new FakeMessageHandler(),
            NullLogger.Instance,
            sessionManager: manager);
        manager?.Register(session);
        return session;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxSessions_ZeroOrNegative_IsUnlimited(int maxSessions)
    {
        var manager = Manager(new SmppServerOptions { MaxSessions = maxSessions });

        Assert.All(Enumerable.Range(0, 50), _ => Assert.True(manager.TryReserveSlot()));
    }

    [Fact]
    public void MaxSessions_IsNotExceededByConcurrentConnections()
    {
        var manager = Manager(new SmppServerOptions { MaxSessions = 5 });

        var granted = 0;
        Parallel.For(0, 200, _ =>
        {
            if (manager.TryReserveSlot())
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(5, granted);
        manager.ReleaseSlot();
        Assert.True(manager.TryReserveSlot());
        Assert.False(manager.TryReserveSlot());
    }

    [Fact]
    public async Task MaxSessionsPerSystemId_CountsBindsStillAuthenticating()
    {
        var options = new SmppServerOptions { MaxSessionsPerSystemId = 2 };
        var manager = Manager(options);
        await using var a = Session(options, manager);
        await using var b = Session(options, manager);
        await using var c = Session(options, manager);

        // None of these sessions is bound yet: the reservation alone must hold the place.
        Assert.True(a.TryReserveSystemId("esme01"));
        Assert.True(b.TryReserveSystemId("esme01"));
        Assert.False(c.TryReserveSystemId("esme01"));
        Assert.True(c.TryReserveSystemId("other"));

        // A failed bind releases its place; so does removing the session.
        b.ReleaseSystemId();
        Assert.True(c.TryReserveSystemId("esme01"));
        manager.Remove(a);
        Assert.True(b.TryReserveSystemId("esme01"));
        Assert.Equal(2, manager.CountBySystemId("esme01"));
    }

    [Fact]
    public async Task MaxSubmitsPerSecond_Zero_DisablesThrottling()
    {
        await using var unlimited = Session(new SmppServerOptions { MaxSubmitsPerSecond = 0 });
        await using var limited = Session(new SmppServerOptions { MaxSubmitsPerSecond = 2 });

        Assert.All(Enumerable.Range(0, 500), _ => Assert.True(unlimited.TryConsumeSubmitToken()));
        Assert.True(limited.TryConsumeSubmitToken());
        Assert.True(limited.TryConsumeSubmitToken());
        Assert.False(limited.TryConsumeSubmitToken());
    }

    [Theory]
    [InlineData("plain ascii", DataCoding.ASCII)]
    [InlineData("Ελληνικά, 中文, café", DataCoding.UCS2)]
    public async Task DeliverSm_PreservesText(string text, DataCoding expectedCoding)
    {
        var parsed = await RoundTripAsync(text);

        Assert.Equal(expectedCoding, parsed.DataCoding);
        Assert.Equal(text, parsed.GetMessageText());
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ж")]
    public async Task DeliverSm_CarriesLongTextInMessagePayload(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 700));

        var parsed = await RoundTripAsync(text);

        Assert.Equal(text, parsed.GetMessageText());
    }

    private static async Task<DeliverSm> RoundTripAsync(string text)
    {
        var request = new SmppDeliverRequest
        {
            TenantId = "t",
            SourceAddress = "233200000000",
            DestinationAddress = "1234",
            Message = text
        };
        var bytes = DeliverSmDispatcher.Build(request, Encoding, 1).GetBytes();
        var frame = await new SmppPduChannel(new MemoryStream(bytes), Encoding).ReadAsync(CancellationToken.None);
        return Assert.IsType<DeliverSm>(frame!.Value.Pdu);
    }
}
