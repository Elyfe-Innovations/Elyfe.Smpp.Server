using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Handles <c>query_sm</c> (SMPP §4.5.1): asks the platform bridge for the current message state and returns a
///     <c>query_sm_resp</c>.
/// </summary>
public static class QuerySmHandler
{
    /// <summary>Processes a query_sm request.</summary>
    public static async Task HandleAsync(ISmppSessionContext ctx, QuerySm request, CancellationToken cancellationToken)
    {
        var header = new PDUHeader(CommandType.QuerySmResp, request.Header.SequenceNumber);
        var response = (QuerySmResp)PDU.CreatePDU(header, ctx.Encoding);
        response.MessageId = request.MessageID;

        if (ctx.Status is not (SmppSessionStatus.BoundTx or SmppSessionStatus.BoundTrx))
        {
            response.Header.ErrorCode = SmppErrorCode.ESME_RINVBNDSTS;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var state = await ctx.MessageHandler
                .QueryMessageStateAsync(ctx.TenantId!, request.MessageID, cancellationToken)
                .ConfigureAwait(false);

            if (state is null)
            {
                response.Header.ErrorCode = SmppErrorCode.ESME_RQUERYFAIL;
            }
            else
            {
                response.MessageState = (MessageState)state.Value;
                response.Header.ErrorCode = SmppErrorCode.ESME_ROK;
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "query_sm bridge error for session {SessionId}", ctx.SessionId);
            response.Header.ErrorCode = SmppErrorCode.ESME_RQUERYFAIL;
        }

        await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
