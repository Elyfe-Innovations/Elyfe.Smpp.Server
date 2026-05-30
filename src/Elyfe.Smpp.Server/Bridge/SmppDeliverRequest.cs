namespace Elyfe.Smpp.Server.Bridge;

/// <summary>
///     A mobile-originated message or delivery receipt the platform wants to push to a bound ESME
///     via <c>deliver_sm</c>.
/// </summary>
public sealed class SmppDeliverRequest
{
    /// <summary>The tenant the message belongs to. Used to locate the ESME session(s) to deliver to.</summary>
    public required string TenantId { get; init; }

    /// <summary>The source address (originator) for the deliver_sm.</summary>
    public required string SourceAddress { get; init; }

    /// <summary>The destination address (the ESME's served address).</summary>
    public required string DestinationAddress { get; init; }

    /// <summary>The message text, or for a delivery receipt the formatted receipt body.</summary>
    public required string Message { get; init; }

    /// <summary>Whether this deliver_sm represents a delivery receipt (esm_class receipt bit set).</summary>
    public bool IsDeliveryReceipt { get; init; }

    /// <summary>The platform message id this receipt relates to (for receipts only).</summary>
    public string? ReceiptedMessageId { get; init; }
}
