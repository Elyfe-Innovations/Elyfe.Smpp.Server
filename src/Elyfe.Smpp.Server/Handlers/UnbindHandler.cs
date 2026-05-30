using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Handles <c>unbind</c> (SMPP §4.2.2): acknowledges with <c>unbind_resp</c> and transitions the session to the
///     unbound state so the read loop can close the connection gracefully.
/// </summary>
public static class UnbindHandler
{
    /// <summary>Processes an unbind request.</summary>
    public static async Task HandleAsync(ISmppSessionContext ctx, Unbind request, CancellationToken cancellationToken)
    {
        var header = new PDUHeader(CommandType.UnBindResp, request.Header.SequenceNumber);
        var response = PDU.CreatePDU(header, ctx.Encoding);
        await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
        ctx.Logger.LogInformation("Session {SessionId} ({SystemId}) unbound", ctx.SessionId, ctx.SystemId);
        ctx.MarkUnbound();
    }
}
