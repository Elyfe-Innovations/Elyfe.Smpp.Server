using Elyfe.Smpp.Server.Authentication;
using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using Microsoft.Extensions.Logging;

namespace Elyfe.Smpp.Server.Handlers;

/// <summary>
///     Handles <c>bind_transmitter</c>, <c>bind_receiver</c> and <c>bind_transceiver</c> requests:
///     negotiates the interface version, authenticates the credentials and transitions the session into a
///     bound state. Implements SMPP §4.1.1.
/// </summary>
public static class BindRequestHandler
{
    /// <summary>
    ///     Processes a bind request.
    /// </summary>
    public static async Task HandleAsync(ISmppSessionContext ctx, BindRequest request, CancellationToken cancellationToken)
    {
        var response = (BindResponse)request.CreateDefaultResponse();
        response.SystemID = ctx.Options.SystemId;

        if (ctx.Status != SmppSessionStatus.Open)
        {
            ctx.Logger.LogWarning("Bind rejected for session {SessionId}: already bound", ctx.SessionId);
            response.Header.ErrorCode = SmppErrorCode.ESME_RALYBND;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        var version = request.InterfaceVersion;
        if (version < ctx.Options.MinInterfaceVersion || version > ctx.Options.MaxInterfaceVersion)
        {
            ctx.Logger.LogWarning(
                "Bind rejected for session {SessionId}: unsupported interface_version 0x{Version:X2}",
                ctx.SessionId,
                version);
            response.Header.ErrorCode = SmppErrorCode.ESME_RINVBNDSTS;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SystemID))
        {
            response.Header.ErrorCode = SmppErrorCode.ESME_RINVSYSID;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        var credentials = new SmppCredentials(
            request.SystemID,
            request.Password ?? string.Empty,
            request.SystemType,
            ctx.RemoteAddress);

        AuthenticationResult auth;
        try
        {
            auth = await ctx.Authenticator.AuthenticateAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Authentication error for session {SessionId} system_id {SystemId}",
                ctx.SessionId, request.SystemID);
            response.Header.ErrorCode = SmppErrorCode.ESME_RSYSERR;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!auth.Succeeded || auth.TenantId is null)
        {
            ctx.Logger.LogWarning("Bind authentication failed for system_id {SystemId}: {Reason}",
                request.SystemID, auth.FailureReason);
            response.Header.ErrorCode = SmppErrorCode.ESME_RBINDFAIL;
            await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }

        var mode = request.Header.CommandType switch
        {
            CommandType.BindTransmitter => SmppBindMode.Transmitter,
            CommandType.BindReceiver => SmppBindMode.Receiver,
            _ => SmppBindMode.Transceiver
        };

        ctx.MarkBound(mode, request.SystemID, auth.TenantId, version);
        response.Header.ErrorCode = SmppErrorCode.ESME_ROK;
        await ctx.SendAsync(response, cancellationToken).ConfigureAwait(false);

        ctx.Logger.LogInformation(
            "Session {SessionId} bound as {Mode} for system_id {SystemId} (tenant {TenantId}, v0x{Version:X2})",
            ctx.SessionId, mode, request.SystemID, auth.TenantId, version);
    }
}
