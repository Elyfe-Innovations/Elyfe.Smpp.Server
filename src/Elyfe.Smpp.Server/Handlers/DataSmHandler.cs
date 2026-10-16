using Elyfe.Smpp.Server.Bridge;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Handles <c>data_sm</c> (SMPP §4.2.3) when originated by an ESME: treats it like a submit and bridges the payload
///     to the platform, returning a <c>data_sm_resp</c> with the message id.
/// </summary>
public static class DataSmHandler
{
    /// <summary>Processes a data_sm request.</summary>
    public static async Task HandleAsync(ISmppSessionContext ctx, DataSm dataSm, CancellationToken cancellationToken)
    {
        var header = new PDUHeader(CommandType.DataSmResp, dataSm.Header.SequenceNumber);
        var response = (DataSmResp)PDU.CreatePDU(header, ctx.Encoding);

        if (ctx.Status is not (SmppSessionStatus.BoundTx or SmppSessionStatus.BoundTrx))
        {
            response.Header.ErrorCode = SmppErrorCode.ESME_RINVBNDSTS;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!ctx.TryConsumeSubmitToken())
        {
            response.Header.ErrorCode = SmppErrorCode.ESME_RTHROTTLED;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = new SmppSubmitRequest
        {
            TenantId = ctx.TenantId!,
            SystemId = ctx.SystemId!,
            SourceAddress = dataSm.SourceAddress.Address ?? string.Empty,
            DestinationAddress = dataSm.DestinationAddress.Address ?? string.Empty,
            Message = dataSm.GetMessageText() ?? string.Empty,
            RequestDeliveryReceipt = dataSm.RegisteredDelivery != RegisteredDelivery.None,
            DataCoding = (byte)dataSm.DataCoding,
            RemoteAddress = ctx.RemoteAddress
        };

        try
        {
            var result = await ctx.MessageHandler.HandleSubmitAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Accepted)
            {
                response.MessageID = result.MessageId;
                response.Header.ErrorCode = SmppErrorCode.ESME_ROK;
            }
            else
            {
                response.Header.ErrorCode = SmppErrorCode.ESME_RSUBMITFAIL;
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "data_sm bridge error for session {SessionId}", ctx.SessionId);
            response.Header.ErrorCode = SmppErrorCode.ESME_RSYSERR;
        }

        await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
