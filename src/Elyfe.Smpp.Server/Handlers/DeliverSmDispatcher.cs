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
    // SMPP v3.4 §5.2.21 allows sm_length 1-254 (v5.0 allows 255); 254 is valid for both.
    private const int MaxShortMessageBytes = 254;

    // receipted_message_id is a C-Octet String of 1-65 octets including the terminator (v3.4 §5.3.2.12).
    private const int MaxReceiptedMessageIdLength = 64;

    /// <summary>
    ///     Constructs a <c>deliver_sm</c> PDU from a platform deliver request. Delivery receipts set the
    ///     <see cref="EsmClass.DeliveryReceipt" /> indicator in <c>esm_class</c>. Text that is not pure ASCII is sent
    ///     as UCS2, and a body too long for <c>short_message</c> is carried in the <c>message_payload</c> TLV, so the
    ///     message is never altered or cut. Receipts also carry the <c>receipted_message_id</c> and
    ///     <c>message_state</c> TLVs when supplied.
    /// </summary>
    /// <param name="request">The platform deliver request.</param>
    /// <param name="encoding">The SMPP encoding service.</param>
    /// <param name="sequenceNumber">The server-originated sequence number.</param>
    /// <param name="interfaceVersion">
    ///     The interface version the ESME bound with. TLVs must not be sent to a pre-v3.4 ESME (v5.0 §2.11.2), so for
    ///     such a session the receipt TLVs are omitted and a body too long for <c>short_message</c> is refused.
    /// </param>
    public static DeliverSm Build(
        SmppDeliverRequest request,
        SmppEncodingService encoding,
        uint sequenceNumber,
        byte interfaceVersion = (byte)SmppInterfaceVersion.V34)
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

        var tlvsAllowed = interfaceVersion >= (byte)SmppInterfaceVersion.V34;
        if (bytes.Length > MaxShortMessageBytes && !tlvsAllowed)
        {
            throw new InvalidOperationException(
                $"Message of {bytes.Length} bytes cannot be delivered to a pre-v3.4 ESME: it exceeds short_message "
                + "and TLVs are not permitted.");
        }

        if (request.ReceiptedMessageId is { Length: > MaxReceiptedMessageIdLength })
        {
            throw new ArgumentException(
                $"ReceiptedMessageId exceeds {MaxReceiptedMessageIdLength} characters.", nameof(request));
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

        if (request.IsDeliveryReceipt && tlvsAllowed)
        {
            if (!string.IsNullOrEmpty(request.ReceiptedMessageId))
            {
                deliver.SetOptionalParamString(Tag.receipted_message_id, request.ReceiptedMessageId, nullTerminated: true);
            }

            deliver.SetOptionalParamByte(Tag.message_state, request.MessageState);
        }

        return deliver;
    }
}
