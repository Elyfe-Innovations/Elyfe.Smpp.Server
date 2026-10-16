using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Handles <c>enquire_link</c> keepalives (SMPP §4.1.2) by echoing an <c>enquire_link_resp</c>.
/// </summary>
public static class EnquireLinkHandler
{
    /// <summary>Processes an enquire_link request.</summary>
    public static async Task HandleAsync(ISmppSessionContext ctx, EnquireLink request, CancellationToken cancellationToken)
    {
        var header = new PDUHeader(CommandType.EnquireLinkResp, request.Header.SequenceNumber);
        var response = PDU.CreatePDU(header, ctx.Encoding);
        await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
