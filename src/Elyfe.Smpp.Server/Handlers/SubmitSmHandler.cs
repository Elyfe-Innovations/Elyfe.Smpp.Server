using Elyfe.Smpp.Server.Bridge;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Handles <c>submit_sm</c>: validates the bind state, bridges the message to the platform and returns a
///     <c>submit_sm_resp</c> carrying the platform message id. Implements SMPP §4.2.1.
/// </summary>
public static class SubmitSmHandler
{
    /// <summary>
    ///     Processes a submit_sm request.
    /// </summary>
    public static async Task HandleAsync(ISmppSessionContext ctx, SubmitSm submit, CancellationToken cancellationToken)
    {
        var header = new PDUHeader(CommandType.SubmitSmResp, submit.Header.SequenceNumber);
        var response = (SubmitSmResp)PDU.CreatePDU(header, ctx.Encoding);

        if (ctx.Status is not (SmppSessionStatus.BoundTx or SmppSessionStatus.BoundTrx))
        {
            response.Header.ErrorCode = SmppErrorCode.ESME_RINVBNDSTS;
            ctx.Metrics.RecordSubmit("not_bound");
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!ctx.TryConsumeSubmitToken())
        {
            ctx.Logger.LogWarning("submit_sm throttled for session {SessionId}", ctx.SessionId);
            response.Header.ErrorCode = SmppErrorCode.ESME_RTHROTTLED;
            ctx.Metrics.RecordSubmit("throttled");
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = new SmppSubmitRequest
        {
            TenantId = ctx.TenantId!,
            SystemId = ctx.SystemId!,
            SourceAddress = submit.SourceAddress.Address ?? string.Empty,
            DestinationAddress = submit.DestinationAddress.Address ?? string.Empty,
            Message = submit.GetMessageText() ?? string.Empty,
            RequestDeliveryReceipt = submit.RegisteredDelivery != RegisteredDelivery.None,
            DataCoding = (byte)submit.DataCoding,
            RemoteAddress = ctx.RemoteAddress
        };

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var result = await ctx.MessageHandler.HandleSubmitAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Accepted)
            {
                response.MessageID = result.MessageId;
                response.Header.ErrorCode = SmppErrorCode.ESME_ROK;
                ctx.Metrics.RecordSubmit("accepted");
            }
            else
            {
                ctx.Logger.LogWarning("submit_sm rejected by platform for session {SessionId}: {Reason}",
                    ctx.SessionId, result.FailureReason);
                response.Header.ErrorCode = SmppErrorCode.ESME_RSUBMITFAIL;
                ctx.Metrics.RecordSubmit("rejected");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "submit_sm bridge error for session {SessionId}", ctx.SessionId);
            response.Header.ErrorCode = SmppErrorCode.ESME_RSYSERR;
            ctx.Metrics.RecordSubmit("error");
            ctx.Metrics.RecordError("submit_bridge");
        }
        finally
        {
            ctx.Metrics.RecordSubmitDuration(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }

        await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
