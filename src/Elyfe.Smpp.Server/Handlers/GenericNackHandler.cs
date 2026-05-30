using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Builds <c>generic_nack</c> PDUs (SMPP §4.3.1) used to reject malformed or unsupported requests while echoing the
///     original sequence number where available.
/// </summary>
public static class GenericNackHandler
{
    /// <summary>Builds a generic_nack carrying the supplied error code and sequence number.</summary>
    public static PDU Build(SmppEncodingService encoding, uint sequenceNumber, SmppErrorCode errorCode)
    {
        var header = new PDUHeader(CommandType.GenericNack, sequenceNumber)
        {
            ErrorCode = errorCode
        };
        return PDU.CreatePDU(header, encoding);
    }
}
