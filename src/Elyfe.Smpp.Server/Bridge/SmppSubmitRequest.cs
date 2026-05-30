namespace Elyfe.Smpp.Server.Bridge;

/// <summary>
///     A message submitted by an ESME via <c>submit_sm</c> / <c>submit_multi</c> / <c>data_sm</c>,
///     normalised into a transport-agnostic shape for the platform.
/// </summary>
public sealed class SmppSubmitRequest
{
    /// <summary>The tenant resolved from the bound <c>system_id</c> at authentication time.</summary>
    public required string TenantId { get; init; }

    /// <summary>The bound <c>system_id</c> that submitted the message.</summary>
    public required string SystemId { get; init; }

    /// <summary>The source address (sender id) from the PDU.</summary>
    public required string SourceAddress { get; init; }

    /// <summary>The destination address (recipient MSISDN) from the PDU.</summary>
    public required string DestinationAddress { get; init; }

    /// <summary>The decoded message text.</summary>
    public required string Message { get; init; }

    /// <summary>Whether the ESME requested a delivery receipt (registered_delivery != 0).</summary>
    public bool RequestDeliveryReceipt { get; init; }

    /// <summary>The raw SMPP data_coding value of the submitted message.</summary>
    public byte DataCoding { get; init; }

    /// <summary>The remote IP address of the submitting ESME, when available.</summary>
    public string? RemoteAddress { get; init; }
}

/// <summary>
///     The result of bridging a <see cref="SmppSubmitRequest" /> to the platform.
/// </summary>
/// <param name="Accepted">Whether the platform accepted the message for delivery.</param>
/// <param name="MessageId">
///     The platform message id echoed back to the ESME in <c>submit_sm_resp</c>. This id is later used to
///     correlate delivery receipts.
/// </param>
/// <param name="FailureReason">A diagnostic reason when <paramref name="Accepted" /> is <c>false</c>.</param>
public readonly record struct SmppSubmitResult(bool Accepted, string MessageId, string? FailureReason)
{
    /// <summary>Creates an accepted result carrying the platform message id.</summary>
    public static SmppSubmitResult Success(string messageId) => new(true, messageId, null);

    /// <summary>Creates a rejected result with an optional diagnostic reason.</summary>
    public static SmppSubmitResult Failure(string? reason = null) => new(false, string.Empty, reason);
}
