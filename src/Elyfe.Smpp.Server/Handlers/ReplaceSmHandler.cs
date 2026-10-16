using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Handles <c>replace_sm</c> (SMPP §4.10.1): replaces the short message of a previously submitted message via the
///     platform bridge and returns a <c>replace_sm_resp</c>.
/// </summary>
public static class ReplaceSmHandler
{
    /// <summary>Processes a replace_sm request.</summary>
    public static async Task HandleAsync(ISmppSessionContext ctx, ReplaceSm request, CancellationToken cancellationToken)
    {
        var header = new PDUHeader(CommandType.ReplaceSmResp, request.Header.SequenceNumber);
        var response = PDU.CreatePDU(header, ctx.Encoding);

        if (ctx.Status is not (SmppSessionStatus.BoundTx or SmppSessionStatus.BoundTrx))
        {
            response.Header.ErrorCode = SmppErrorCode.ESME_RINVBNDSTS;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var replaced = await ctx.MessageHandler
                .ReplaceMessageAsync(ctx.TenantId!, request.MessageID, request.ShortMessage ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
            response.Header.ErrorCode = replaced ? SmppErrorCode.ESME_ROK : SmppErrorCode.ESME_RREPLACEFAIL;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "replace_sm bridge error for session {SessionId}", ctx.SessionId);
            response.Header.ErrorCode = SmppErrorCode.ESME_RREPLACEFAIL;
        }

        await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
