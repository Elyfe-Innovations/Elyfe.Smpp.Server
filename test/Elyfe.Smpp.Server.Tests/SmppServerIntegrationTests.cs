using System.Net;
using System.Net.Sockets;
using Elyfe.Smpp.Server.Authentication;
using Elyfe.Smpp.Server.Bridge;
using Elyfe.Smpp.Server.Networking;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using JamaaTech.Smpp.Net.Lib.Protocol.Tlv;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elyfe.Smpp.Server.Tests;

/// <summary>
///     End-to-end test that drives a real <see cref="SmppServer" /> over a loopback TCP connection using a simulated
///     SMPP client: bind → submit_sm → enquire_link → unbind.
/// </summary>
public class SmppServerIntegrationTests
{
    private readonly SmppEncodingService _encoding = new();

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static SmppServer BuildServer(SmppServerOptions options, IMessageHandler handler)
    {
        var sessionManager = new SessionManager(options, NullLogger<SessionManager>.Instance);
        var authOptions = new SmppAuthenticationOptions
        {
            Accounts = [new SmppAccount { SystemId = "esme01", Password = "secret", TenantId = "tenant-x" }]
        };
        var authenticator = new ConfigBasedAuthenticator(new StaticOptionsMonitor<SmppAuthenticationOptions>(authOptions));
        return new SmppServer(options, sessionManager, authenticator, handler, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task FullLifecycle_BindSubmitEnquireUnbind_Succeeds()
    {
        var options = new SmppServerOptions
        {
            Port = GetFreePort(),
            BindAddress = "127.0.0.1",
            SystemId = "TestSMSC"
        };
        var handler = new FakeMessageHandler { SubmitResult = SmppSubmitResult.Success("platform-msg-1") };
        await using var server = BuildServer(options, handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, options.Port, cts.Token);
        await using var stream = client.GetStream();
        var channel = new SmppPduChannel(stream, _encoding);

        // --- bind_transmitter ---
        var bind = (BindTransmitter)PDU.CreatePDU(new PDUHeader(CommandType.BindTransmitter, 1), _encoding);
        bind.SystemID = "esme01";
        bind.Password = "secret";
        bind.InterfaceVersion = 0x34;
        await channel.WriteAsync(bind, cts.Token);

        var bindResp = await channel.ReadAsync(cts.Token);
        Assert.NotNull(bindResp);
        var bindRespPdu = Assert.IsType<BindTransmitterResp>(bindResp!.Value.Pdu);
        Assert.Equal(SmppErrorCode.ESME_ROK, bindRespPdu.Header.ErrorCode);
        Assert.Equal("TestSMSC", bindRespPdu.SystemID);
        Assert.Equal((byte)0x34, bindRespPdu.GetOptionalParamByte(Tag.SC_interface_version));

        // --- submit_sm ---
        var submit = (SubmitSm)PDU.CreatePDU(new PDUHeader(CommandType.SubmitSm, 2), _encoding);
        submit.SourceAddress.Address = "1234";
        submit.DestinationAddress.Address = "233200000000";
        submit.SetMessageText("integration test", DataCoding.ASCII);
        await channel.WriteAsync(submit, cts.Token);

        var submitResp = await channel.ReadAsync(cts.Token);
        Assert.NotNull(submitResp);
        var submitRespPdu = Assert.IsType<SubmitSmResp>(submitResp!.Value.Pdu);
        Assert.Equal(SmppErrorCode.ESME_ROK, submitRespPdu.Header.ErrorCode);
        Assert.Equal("platform-msg-1", submitRespPdu.MessageID);
        Assert.Equal("tenant-x", handler.LastSubmit?.TenantId);

        // --- enquire_link ---
        var enquire = (EnquireLink)PDU.CreatePDU(new PDUHeader(CommandType.EnquireLink, 3), _encoding);
        await channel.WriteAsync(enquire, cts.Token);
        var enquireResp = await channel.ReadAsync(cts.Token);
        Assert.NotNull(enquireResp);
        Assert.Equal(CommandType.EnquireLinkResp, enquireResp!.Value.Header.CommandType);

        // --- unbind ---
        var unbind = (Unbind)PDU.CreatePDU(new PDUHeader(CommandType.UnBind, 4), _encoding);
        await channel.WriteAsync(unbind, cts.Token);
        var unbindResp = await channel.ReadAsync(cts.Token);
        Assert.NotNull(unbindResp);
        Assert.Equal(CommandType.UnBindResp, unbindResp!.Value.Header.CommandType);

        await server.StopAsync(cts.Token);
    }

    [Fact]
    public async Task FailedBind_ReturnsHeaderOnlyResponse_AndBadLengthGetsGenericNack()
    {
        var options = new SmppServerOptions { Port = GetFreePort(), BindAddress = "127.0.0.1", SystemId = "TestSMSC" };
        await using var server = BuildServer(options, new FakeMessageHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, options.Port, cts.Token);
        var stream = client.GetStream();
        var channel = new SmppPduChannel(stream, _encoding);

        var bind = (BindTransmitter)PDU.CreatePDU(new PDUHeader(CommandType.BindTransmitter, 5), _encoding);
        bind.SystemID = "esme01";
        bind.Password = "wrong";
        bind.InterfaceVersion = 0x34;
        await channel.WriteAsync(bind, cts.Token);

        // v3.4 §4.1.2: the body (system_id) is not returned when command_status is non-zero.
        var resp = (await channel.ReadAsync(cts.Token))!.Value;
        Assert.Equal(CommandType.BindTransmitterResp, resp.Header.CommandType);
        Assert.Equal(SmppErrorCode.ESME_RBINDFAIL, resp.Header.ErrorCode);
        Assert.Equal(5u, resp.Header.SequenceNumber);
        Assert.Equal(16u, resp.Header.CommandLength);

        // v3.4 §4.3: an invalid command_length is answered with a generic_nack before the connection closes.
        await stream.WriteAsync(new byte[] { 0, 0, 0, 4, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 9 }, cts.Token);
        var nack = (await channel.ReadAsync(cts.Token))!.Value;
        Assert.Equal(CommandType.GenericNack, nack.Header.CommandType);
        Assert.Equal(SmppErrorCode.ESME_RINVCMDLEN, nack.Header.ErrorCode);
        Assert.Null(await channel.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task UnsupportedInterfaceVersion_IsRejectedWithBindFail()
    {
        var options = new SmppServerOptions { Port = GetFreePort(), BindAddress = "127.0.0.1", SystemId = "TestSMSC" };
        await using var server = BuildServer(options, new FakeMessageHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, options.Port, cts.Token);
        var channel = new SmppPduChannel(client.GetStream(), _encoding);

        var bind = (BindTransceiver)PDU.CreatePDU(new PDUHeader(CommandType.BindTransceiver, 3), _encoding);
        bind.SystemID = "esme01";
        bind.Password = "secret";
        bind.InterfaceVersion = 0x33;
        await channel.WriteAsync(bind, cts.Token);

        var resp = (await channel.ReadAsync(cts.Token))!.Value;
        Assert.Equal(CommandType.BindTransceiverResp, resp.Header.CommandType);
        Assert.Equal(SmppErrorCode.ESME_RBINDFAIL, resp.Header.ErrorCode);

        await server.StopAsync(cts.Token);
    }

    [Fact]
    public async Task MalformedBody_GetsMatchingErrorResponse_AndSessionStaysUsable()
    {
        var options = new SmppServerOptions
        {
            Port = GetFreePort(),
            BindAddress = "127.0.0.1",
            SystemId = "TestSMSC",
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(200)
        };
        var handler = new FakeMessageHandler { SubmitResult = SmppSubmitResult.Success("msg-1") };
        await using var server = BuildServer(options, handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        var channel = await BindTransceiverAsync(client, options.Port, cts.Token);
        var stream = client.GetStream();

        // A submit_sm (0x00000004) whose body is two bytes: far short of its mandatory fields.
        await stream.WriteAsync(new byte[] { 0, 0, 0, 18, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 7, 0x41, 0 }, cts.Token);
        var truncated = (await channel.ReadAsync(cts.Token))!.Value;
        Assert.Equal(CommandType.SubmitSmResp, truncated.Header.CommandType);
        Assert.NotEqual(SmppErrorCode.ESME_ROK, truncated.Header.ErrorCode);
        Assert.Equal(7u, truncated.Header.SequenceNumber);
        Assert.Equal(16u, truncated.Header.CommandLength);

        // A header-only submit_sm is malformed too, not an empty message.
        await stream.WriteAsync(new byte[] { 0, 0, 0, 16, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 8 }, cts.Token);
        var empty = (await channel.ReadAsync(cts.Token))!.Value;
        Assert.Equal(CommandType.SubmitSmResp, empty.Header.CommandType);
        Assert.NotEqual(SmppErrorCode.ESME_ROK, empty.Header.ErrorCode);
        Assert.Equal(8u, empty.Header.SequenceNumber);
        Assert.Null(handler.LastSubmit);

        // The session is still bound and serves the next well-formed request.
        var submit = (SubmitSm)PDU.CreatePDU(new PDUHeader(CommandType.SubmitSm, 9), _encoding);
        submit.SourceAddress.Address = "1234";
        submit.DestinationAddress.Address = "233200000000";
        submit.SetMessageText("still here", DataCoding.ASCII);
        await channel.WriteAsync(submit, cts.Token);
        var ok = (await channel.ReadAsync(cts.Token))!.Value;
        Assert.Equal(SmppErrorCode.ESME_ROK, ok.Header.ErrorCode);
        Assert.Equal(9u, ok.Header.SequenceNumber);

        await server.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Deliver_IsAcknowledged_WhenResponseBodyCannotBeParsed()
    {
        var options = new SmppServerOptions
        {
            Port = GetFreePort(),
            BindAddress = "127.0.0.1",
            SystemId = "TestSMSC",
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(200)
        };
        await using var server = BuildServer(options, new FakeMessageHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        var channel = await BindTransceiverAsync(client, options.Port, cts.Token);
        var stream = client.GetStream();

        var delivery = server.DeliverAsync(Deliver("hello"), cts.Token);
        var deliver = (await channel.ReadAsync(cts.Token))!.Value;
        var seq = deliver.Header.SequenceNumber;

        // deliver_sm_resp with a NULL message_id followed by a v5.0 TLV (network_error_code), which the PDU
        // library does not accept on this PDU.
        byte[] resp =
        [
            0, 0, 0, 24, 0x80, 0, 0, 5, 0, 0, 0, 0,
            (byte)(seq >> 24), (byte)(seq >> 16), (byte)(seq >> 8), (byte)seq,
            0, 0x04, 0x23, 0, 3, 3, 0, 0
        ];
        await stream.WriteAsync(resp, cts.Token);

        Assert.True(await delivery);

        await server.StopAsync(cts.Token);
    }

    private async Task<SmppPduChannel> BindTransceiverAsync(TcpClient client, int port, CancellationToken ct)
    {
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        var channel = new SmppPduChannel(client.GetStream(), _encoding);

        var bind = (BindTransceiver)PDU.CreatePDU(new PDUHeader(CommandType.BindTransceiver, 1), _encoding);
        bind.SystemID = "esme01";
        bind.Password = "secret";
        bind.InterfaceVersion = 0x34;
        await channel.WriteAsync(bind, ct);

        var bindResp = await channel.ReadAsync(ct);
        Assert.Equal(SmppErrorCode.ESME_ROK, bindResp!.Value.Header.ErrorCode);
        return channel;
    }

    private static SmppDeliverRequest Deliver(string message) => new()
    {
        TenantId = "tenant-x",
        SourceAddress = "233200000000",
        DestinationAddress = "1234",
        Message = message
    };

    [Fact]
    public async Task Deliver_ReportsEsmeAcknowledgement_AndDoesNotNackResponses()
    {
        var options = new SmppServerOptions
        {
            Port = GetFreePort(),
            BindAddress = "127.0.0.1",
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(200) // This client never answers the shutdown unbind.
        };
        await using var server = BuildServer(options, new FakeMessageHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        var channel = await BindTransceiverAsync(client, options.Port, cts.Token);

        // Acknowledged delivery, with non-ASCII text arriving intact.
        var accepted = server.DeliverAsync(Deliver("Olá — Ωmega ✓"), cts.Token);
        var first = Assert.IsType<DeliverSm>((await channel.ReadAsync(cts.Token))!.Value.Pdu);
        Assert.Equal("Olá — Ωmega ✓", first.GetMessageText());
        Assert.False(accepted.IsCompleted);
        await channel.WriteAsync(first.CreateDefaultResponse(), cts.Token);
        Assert.True(await accepted);

        // Rejected delivery surfaces as a failure.
        var rejected = server.DeliverAsync(Deliver("second"), cts.Token);
        var second = Assert.IsType<DeliverSm>((await channel.ReadAsync(cts.Token))!.Value.Pdu);
        var errorResp = second.CreateDefaultResponse();
        errorResp.Header.ErrorCode = SmppErrorCode.ESME_RSYSERR;
        await channel.WriteAsync(errorResp, cts.Token);
        Assert.False(await rejected);

        // An unsolicited enquire_link_resp is ignored; the next frame is our enquire_link_resp, not a generic_nack.
        await channel.WriteAsync(PDU.CreatePDU(new PDUHeader(CommandType.EnquireLinkResp, 99), _encoding), cts.Token);
        await channel.WriteAsync(PDU.CreatePDU(new PDUHeader(CommandType.EnquireLink, 7), _encoding), cts.Token);
        var next = await channel.ReadAsync(cts.Token);
        Assert.Equal(CommandType.EnquireLinkResp, next!.Value.Header.CommandType);
        Assert.Equal(7u, next.Value.Header.SequenceNumber);

        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Deliver_HonoursWindow_AndFailsWhenEsmeNeverRespondsOrDisconnects()
    {
        var options = new SmppServerOptions
        {
            Port = GetFreePort(),
            BindAddress = "127.0.0.1",
            WindowSize = 1
        };
        await using var server = BuildServer(options, new FakeMessageHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        var channel = await BindTransceiverAsync(client, options.Port, cts.Token);

        // The first delivery occupies the single window slot, so the second stays off the wire until it is answered.
        var first = server.DeliverAsync(Deliver("one"), cts.Token);
        var one = Assert.IsType<DeliverSm>((await channel.ReadAsync(cts.Token))!.Value.Pdu);
        var second = server.DeliverAsync(Deliver("two"), cts.Token);
        await Task.Delay(150, cts.Token);
        Assert.Equal(0, client.Available);
        await channel.WriteAsync(one.CreateDefaultResponse(), cts.Token);
        Assert.True(await first);
        var two = Assert.IsType<DeliverSm>((await channel.ReadAsync(cts.Token))!.Value.Pdu);
        Assert.Equal("two", two.GetMessageText());
        await channel.WriteAsync(two.CreateDefaultResponse(), cts.Token);
        Assert.True(await second);

        // No deliver_sm_resp within the timeout is a failure.
        options.DeliverResponseTimeout = TimeSpan.FromMilliseconds(200);
        var third = server.DeliverAsync(Deliver("three"), cts.Token);
        Assert.IsType<DeliverSm>((await channel.ReadAsync(cts.Token))!.Value.Pdu);
        Assert.False(await third);

        // So is a disconnect with a delivery outstanding.
        options.DeliverResponseTimeout = TimeSpan.FromSeconds(30);
        var fourth = server.DeliverAsync(Deliver("four"), cts.Token);
        Assert.IsType<DeliverSm>((await channel.ReadAsync(cts.Token))!.Value.Pdu);
        client.Close();
        Assert.False(await fourth);

        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_UnbindsBoundSessions_AfterCompletingInFlightSubmit()
    {
        var options = new SmppServerOptions { Port = GetFreePort(), BindAddress = "127.0.0.1" };
        var release = new TaskCompletionSource<SmppSubmitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeMessageHandler { PendingSubmit = release.Task };
        await using var server = BuildServer(options, handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        var channel = await BindTransceiverAsync(client, options.Port, cts.Token);

        var submit = (SubmitSm)PDU.CreatePDU(new PDUHeader(CommandType.SubmitSm, 2), _encoding);
        submit.SourceAddress.Address = "1234";
        submit.DestinationAddress.Address = "233200000000";
        submit.SetMessageText("in flight", DataCoding.ASCII);
        await channel.WriteAsync(submit, cts.Token);
        await handler.SubmitStarted.Task.WaitAsync(cts.Token);

        var stop = server.StopAsync(CancellationToken.None);

        var unbind = await channel.ReadAsync(cts.Token);
        Assert.Equal(CommandType.UnBind, unbind!.Value.Header.CommandType);
        await channel.WriteAsync(
            PDU.CreatePDU(new PDUHeader(CommandType.UnBindResp, unbind.Value.Header.SequenceNumber), _encoding),
            cts.Token);

        // The submit awaiting the platform still gets its response rather than a dropped connection.
        Assert.False(stop.IsCompleted);
        release.SetResult(SmppSubmitResult.Success("drained-1"));
        var submitResp = Assert.IsType<SubmitSmResp>((await channel.ReadAsync(cts.Token))!.Value.Pdu);
        Assert.Equal(SmppErrorCode.ESME_ROK, submitResp.Header.ErrorCode);
        Assert.Equal("drained-1", submitResp.MessageID);

        await stop.WaitAsync(cts.Token);
        Assert.Null(await channel.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task SubmitBeforeBind_IsRejected()
    {
        var options = new SmppServerOptions
        {
            Port = GetFreePort(),
            BindAddress = "127.0.0.1",
            SystemId = "TestSMSC"
        };
        var handler = new FakeMessageHandler { SubmitResult = SmppSubmitResult.Success("x") };
        await using var server = BuildServer(options, handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, options.Port, cts.Token);
        await using var stream = client.GetStream();
        var channel = new SmppPduChannel(stream, _encoding);

        var submit = (SubmitSm)PDU.CreatePDU(new PDUHeader(CommandType.SubmitSm, 1), _encoding);
        submit.SourceAddress.Address = "1234";
        submit.DestinationAddress.Address = "233200000000";
        submit.SetMessageText("too early", DataCoding.ASCII);
        await channel.WriteAsync(submit, cts.Token);

        var resp = await channel.ReadAsync(cts.Token);
        Assert.NotNull(resp);
        var respPdu = Assert.IsType<SubmitSmResp>(resp!.Value.Pdu);
        Assert.Equal(SmppErrorCode.ESME_RINVBNDSTS, respPdu.Header.ErrorCode);

        await server.StopAsync(cts.Token);
    }
}

/// <summary>A fake <see cref="IMessageHandler" /> capturing the last submit for assertions.</summary>
internal sealed class FakeMessageHandler : IMessageHandler
{
    public SmppSubmitResult SubmitResult { get; set; } = SmppSubmitResult.Failure("not configured");

    /// <summary>When set, submits complete only when this task does.</summary>
    public Task<SmppSubmitResult>? PendingSubmit { get; set; }

    public TaskCompletionSource SubmitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SmppSubmitRequest? LastSubmit { get; private set; }

    public Task<SmppSubmitResult> HandleSubmitAsync(SmppSubmitRequest request, CancellationToken cancellationToken = default)
    {
        LastSubmit = request;
        SubmitStarted.TrySetResult();
        return PendingSubmit ?? Task.FromResult(SubmitResult);
    }

    public Task<byte?> QueryMessageStateAsync(string tenantId, string messageId, CancellationToken cancellationToken = default)
        => Task.FromResult<byte?>(null);

    public Task<bool> CancelMessageAsync(string tenantId, string messageId, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> ReplaceMessageAsync(string tenantId, string messageId, string newMessage, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
