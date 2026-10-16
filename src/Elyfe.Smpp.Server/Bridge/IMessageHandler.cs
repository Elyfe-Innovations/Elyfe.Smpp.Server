namespace Elyfe.Smpp.Server.Bridge;

/// <summary>
///     The bridge between the SMPP server and the host platform. Implemented by the consuming
///     application (for Tech231 this routes to the Orleans <c>ISmsGrain</c> pipeline).
/// </summary>
public interface IMessageHandler
{
    /// <summary>
    ///     Handles an ESME-submitted message (<c>submit_sm</c> / <c>submit_multi</c> / <c>data_sm</c>).
    /// </summary>
    /// <param name="request">The normalised submit request.</param>
    /// <param name="cancellationToken">A token to cancel the platform call.</param>
    /// <returns>The submit result, including the platform message id echoed to the ESME.</returns>
    Task<SmppSubmitResult> HandleSubmitAsync(SmppSubmitRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Queries the delivery status of a previously submitted message (<c>query_sm</c>).
    /// </summary>
    /// <param name="tenantId">The tenant the querying session is bound as.</param>
    /// <param name="messageId">The platform message id to look up.</param>
    /// <param name="cancellationToken">A token to cancel the platform call.</param>
    /// <returns>An SMPP <c>message_state</c> value, or <c>null</c> if the message is unknown.</returns>
    Task<byte?> QueryMessageStateAsync(string tenantId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Attempts to cancel a previously submitted message (<c>cancel_sm</c>).
    /// </summary>
    /// <returns><c>true</c> when the message was cancelled.</returns>
    Task<bool> CancelMessageAsync(string tenantId, string messageId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Attempts to replace the content of a previously submitted message (<c>replace_sm</c>).
    /// </summary>
    /// <returns><c>true</c> when the message was replaced.</returns>
    Task<bool> ReplaceMessageAsync(string tenantId, string messageId, string newMessage, CancellationToken cancellationToken = default);
}
