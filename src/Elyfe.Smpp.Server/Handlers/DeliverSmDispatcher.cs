using System.Text;
using Elyfe.Smpp.Server.Bridge;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;

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
    ///     <see cref="EsmClass.DeliveryReceipt" /> indicator in <c>esm_class</c>.
    /// </summary>
    public static DeliverSm Build(SmppDeliverRequest request, SmppEncodingService encoding, uint sequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(encoding);

        var header = new PDUHeader(CommandType.DeliverSm, sequenceNumber);
        var deliver = new DeliverSm(header, encoding)
        {
            DataCoding = DataCoding.ASCII,
            EsmClass = request.IsDeliveryReceipt ? EsmClass.DeliveryReceipt : EsmClass.Default
        };

        deliver.SourceAddress.Address = request.SourceAddress;
        deliver.DestinationAddress.Address = request.DestinationAddress;

        var bytes = Encoding.ASCII.GetBytes(request.Message ?? string.Empty);
        if (bytes.Length > MaxShortMessageBytes)
        {
            bytes = bytes[..MaxShortMessageBytes];
        }

        deliver.SetMessageBytes(bytes);
        return deliver;
    }
}
