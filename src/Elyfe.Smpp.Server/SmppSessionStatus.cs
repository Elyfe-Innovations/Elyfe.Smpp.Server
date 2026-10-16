namespace Elyfe.Smpp.Server;

/// <summary>
///     The SMPP session lifecycle states per the SMPP specification §2.3.
/// </summary>
public enum SmppSessionStatus
{
    /// <summary>The TCP connection is established but no successful bind has occurred yet.</summary>
    Open,

    /// <summary>Bound as a transmitter (can submit, cannot receive).</summary>
    BoundTx,

    /// <summary>Bound as a receiver (can receive deliver_sm, cannot submit).</summary>
    BoundRx,

    /// <summary>Bound as a transceiver (can both submit and receive).</summary>
    BoundTrx,

    /// <summary>An unbind has been processed; the session is winding down.</summary>
    Unbound,

    /// <summary>The session is closed and its resources released.</summary>
    Closed
}

/// <summary>
///     The bind mode negotiated during a <c>bind_*</c> operation.
/// </summary>
public enum SmppBindMode
{
    /// <summary>Not yet bound.</summary>
    None,

    /// <summary>bind_transmitter.</summary>
    Transmitter,

    /// <summary>bind_receiver.</summary>
    Receiver,

    /// <summary>bind_transceiver.</summary>
    Transceiver
}
