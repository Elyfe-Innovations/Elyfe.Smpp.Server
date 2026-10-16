using System.Net.Sockets;
using Elyfe.Smpp.Server.Authentication;
using Elyfe.Smpp.Server.Bridge;
using Elyfe.Smpp.Server.Handlers;
using Elyfe.Smpp.Server.Networking;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using JamaaTech.Smpp.Net.Lib.Protocol.Tlv;
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

    [Fact]
    public async Task DeliverSm_LongText_UsesMessagePayloadWithZeroSmLength()
    {
        var request = Request(new string('a', 255));

        var bytes = DeliverSmDispatcher.Build(request, Encoding, 1).GetBytes();
        var parsed = await ParseAsync(bytes);

        // sm_length must be 0 and the body must not be duplicated in short_message (v3.4 §4.6.1, §5.2.21).
        var payload = parsed.Tlv.GetTlvByTag(Tag.message_payload);
        Assert.NotNull(payload);
        Assert.Equal(255, payload!.RawValue.Length);
        const int smLengthOffset = 16 + 1 + 15 + 7 + 9; // header, service_type, source, destination, nine fixed fields
        Assert.Equal(0, bytes[smLengthOffset]);
        Assert.Equal(new byte[] { 0x04, 0x24, 0x00, 0xFF }, bytes[(smLengthOffset + 1)..(smLengthOffset + 5)]);
        Assert.Equal(smLengthOffset + 1 + 4 + 255, bytes.Length);
    }

    [Fact]
    public async Task DeliverSm_Receipt_CarriesReceiptedMessageIdAndMessageState()
    {
        var request = new SmppDeliverRequest
        {
            TenantId = "t",
            SourceAddress = "233200000000",
            DestinationAddress = "1234",
            Message = "id:abc123 stat:DELIVRD",
            IsDeliveryReceipt = true,
            ReceiptedMessageId = "abc123",
            MessageState = 2
        };

        var parsed = await ParseAsync(DeliverSmDispatcher.Build(request, Encoding, 1).GetBytes());

        Assert.Equal(EsmClass.DeliveryReceipt, parsed.EsmClass);
        Assert.Equal("abc123", parsed.GetOptionalParamCString(Tag.receipted_message_id));
        Assert.Equal((byte)2, parsed.GetOptionalParamByte(Tag.message_state));
    }

    [Fact]
    public async Task DeliverSm_ToPreV34Esme_NeverCarriesTlvs()
    {
        const byte v33 = 0x33;
        var receipt = new SmppDeliverRequest
        {
            TenantId = "t",
            SourceAddress = "233200000000",
            DestinationAddress = "1234",
            Message = "id:abc123 stat:DELIVRD",
            IsDeliveryReceipt = true,
            ReceiptedMessageId = "abc123",
            MessageState = 2
        };

        var parsed = await ParseAsync(DeliverSmDispatcher.Build(receipt, Encoding, 1, v33).GetBytes());

        Assert.Empty(parsed.Tlv);
        Assert.Throws<InvalidOperationException>(
            () => DeliverSmDispatcher.Build(Request(new string('a', 255)), Encoding, 1, v33));
    }

    private static SmppDeliverRequest Request(string text) => new()
    {
        TenantId = "t",
        SourceAddress = "233200000000",
        DestinationAddress = "1234",
        Message = text
    };

    private static async Task<DeliverSm> ParseAsync(byte[] bytes)
    {
        var frame = await new SmppPduChannel(new MemoryStream(bytes), Encoding).ReadAsync(CancellationToken.None);
        return Assert.IsType<DeliverSm>(frame!.Value.Pdu);
    }

    private static async Task<DeliverSm> RoundTripAsync(string text)
    {
        var request = Request(text);
        var bytes = DeliverSmDispatcher.Build(request, Encoding, 1).GetBytes();
        var frame = await new SmppPduChannel(new MemoryStream(bytes), Encoding).ReadAsync(CancellationToken.None);
        return Assert.IsType<DeliverSm>(frame!.Value.Pdu);
    }
}
