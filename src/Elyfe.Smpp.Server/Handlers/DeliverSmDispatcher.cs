using System.Text;
using Elyfe.Smpp.Server.Bridge;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using JamaaTech.Smpp.Net.Lib.Protocol.Tlv;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Builds server-originated <c>deliver_sm</c> PDUs (SMPP §4.6.1) used to push mobile-originated messages and
///     delivery receipts to a bound receiver/transceiver ESME.
/// </summary>
public static class DeliverSmDispatcher
{
    private const int MaxShortMessageBytes = 254;

    /// <summary>
    ///     Constructs a <c>deliver_sm</c> PDU from a platform deliver request. Delivery receipts set the
    ///     <see cref="EsmClass.DeliveryReceipt" /> indicator in <c>esm_class</c>. Text that is not pure ASCII is sent
    ///     as UCS2, and a body too long for <c>short_message</c> is carried in the <c>message_payload</c> TLV, so the
    ///     message is never altered or cut.
    /// </summary>
    public static DeliverSm Build(SmppDeliverRequest request, SmppEncodingService encoding, uint sequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(encoding);

        var text = request.Message ?? string.Empty;
        var dataCoding = Ascii.IsValid(text) ? DataCoding.ASCII : DataCoding.UCS2;
        var bytes = text.Length == 0 ? [] : encoding.GetBytesFromString(text, dataCoding);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"Message of {bytes.Length} bytes exceeds the {ushort.MaxValue}-byte message_payload limit.",
                nameof(request));
        }

        var header = new PDUHeader(CommandType.DeliverSm, sequenceNumber);
        var deliver = new DeliverSm(header, encoding)
        {
            DataCoding = dataCoding,
            EsmClass = request.IsDeliveryReceipt ? EsmClass.DeliveryReceipt : EsmClass.Default
        };

        deliver.SourceAddress.Address = request.SourceAddress;
        deliver.DestinationAddress.Address = request.DestinationAddress;

        if (bytes.Length <= MaxShortMessageBytes)
        {
            deliver.SetMessageBytes(bytes);
        }
        else
        {
            deliver.SetOptionalParamBytes(Tag.message_payload, bytes);
        }

        return deliver;
    }
}
