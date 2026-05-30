using System.Net;
using System.Net.Sockets;
using Elyfe.Smpp.Server.Authentication;
using Elyfe.Smpp.Server.Bridge;
using Elyfe.Smpp.Server.Networking;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
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

    public SmppSubmitRequest? LastSubmit { get; private set; }

    public Task<SmppSubmitResult> HandleSubmitAsync(SmppSubmitRequest request, CancellationToken cancellationToken = default)
    {
        LastSubmit = request;
        return Task.FromResult(SubmitResult);
    }

    public Task<byte?> QueryMessageStateAsync(string tenantId, string messageId, CancellationToken cancellationToken = default)
        => Task.FromResult<byte?>(null);

    public Task<bool> CancelMessageAsync(string tenantId, string messageId, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> ReplaceMessageAsync(string tenantId, string messageId, string newMessage, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
