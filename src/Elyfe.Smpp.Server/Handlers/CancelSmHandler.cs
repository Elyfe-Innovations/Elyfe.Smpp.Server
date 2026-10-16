using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Handles <c>cancel_sm</c> (SMPP §4.9.1): requests cancellation of a previously submitted message via the platform
///     bridge and returns a <c>cancel_sm_resp</c>.
/// </summary>
public static class CancelSmHandler
{
    /// <summary>Processes a cancel_sm request.</summary>
    public static async Task HandleAsync(ISmppSessionContext ctx, CancelSm request, CancellationToken cancellationToken)
    {
        var header = new PDUHeader(CommandType.CancelSmResp, request.Header.SequenceNumber);
        var response = PDU.CreatePDU(header, ctx.Encoding);

        if (ctx.Status is not (SmppSessionStatus.BoundTx or SmppSessionStatus.BoundTrx))
        {
            response.Header.ErrorCode = SmppErrorCode.ESME_RINVBNDSTS;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var cancelled = await ctx.MessageHandler
                .CancelMessageAsync(ctx.TenantId!, request.MessageID, cancellationToken)
                .ConfigureAwait(false);
            response.Header.ErrorCode = cancelled ? SmppErrorCode.ESME_ROK : SmppErrorCode.ESME_RCANCELFAIL;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "cancel_sm bridge error for session {SessionId}", ctx.SessionId);
            response.Header.ErrorCode = SmppErrorCode.ESME_RCANCELFAIL;
        }

        await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
