using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using JamaaTech.Smpp.Net.Lib.Util;
using Xunit;

namespace Elyfe.Smpp.Server.Tests;

/// <summary>
///     Round-trip serialization tests over the reused PDU library to confirm the server can correctly encode and decode
///     the request and response PDUs it relies on.
/// </summary>
public class PduRoundTripTests
{
    private readonly SmppEncodingService _encoding = new();

    private PDU Reparse(PDU original)
    {
        var bytes = original.GetBytes();
        var header = PDUHeader.Parse(new ByteBuffer(bytes[..16]), _encoding);
        var pdu = PDU.CreatePDU(header, _encoding);
        if (bytes.Length > 16)
        {
            pdu.SetBodyData(new ByteBuffer(bytes[16..]));
        }

        return pdu;
    }

    [Fact]
    public void BindTransmitter_RoundTrips()
    {
        var bind = new BindTransmitter(_encoding)
        {
            SystemID = "esme01",
            Password = "secret",
            SystemType = "test",
            InterfaceVersion = 0x34
        };

        var parsed = Assert.IsType<BindTransmitter>(Reparse(bind));
        Assert.Equal("esme01", parsed.SystemID);
        Assert.Equal("secret", parsed.Password);
        Assert.Equal(0x34, parsed.InterfaceVersion);
    }

    [Fact]
    public void SubmitSm_RoundTrips()
    {
        var submit = new SubmitSm(_encoding);
        submit.SourceAddress.Address = "1234";
        submit.DestinationAddress.Address = "233200000000";
        submit.DataCoding = DataCoding.ASCII;
        submit.SetMessageText("hello world", DataCoding.ASCII);

        var parsed = Assert.IsType<SubmitSm>(Reparse(submit));
        Assert.Equal("1234", parsed.SourceAddress.Address);
        Assert.Equal("233200000000", parsed.DestinationAddress.Address);
        Assert.Equal("hello world", parsed.GetMessageText());
    }

    [Fact]
    public void SubmitSmResp_BuiltWithCorrectCommandId()
    {
        // Guards against the library's CreateDefaultResponse bug by building the response explicitly.
        var header = new PDUHeader(CommandType.SubmitSmResp, 42);
        var resp = (SubmitSmResp)PDU.CreatePDU(header, _encoding);
        resp.MessageID = "msg-123";

        var parsed = Assert.IsType<SubmitSmResp>(Reparse(resp));
        Assert.Equal(CommandType.SubmitSmResp, parsed.Header.CommandType);
        Assert.Equal(42u, parsed.Header.SequenceNumber);
        Assert.Equal("msg-123", parsed.MessageID);
    }

    [Fact]
    public void DeliverSm_DeliveryReceipt_RoundTrips()
    {
        var header = new PDUHeader(CommandType.DeliverSm, 7);
        var deliver = new DeliverSm(header, _encoding)
        {
            DataCoding = DataCoding.ASCII,
            EsmClass = EsmClass.DeliveryReceipt
        };
        deliver.SourceAddress.Address = "233200000000";
        deliver.DestinationAddress.Address = "1234";
        deliver.SetMessageBytes("id:123 stat:DELIVRD"u8.ToArray());

        var parsed = Assert.IsType<DeliverSm>(Reparse(deliver));
        Assert.Equal(CommandType.DeliverSm, parsed.Header.CommandType);
        Assert.Equal(EsmClass.DeliveryReceipt, parsed.EsmClass & EsmClass.DeliveryReceipt);
    }
}
