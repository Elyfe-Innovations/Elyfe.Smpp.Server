using JamaaTech.Smpp.Net.Lib.Protocol;

namespace Elyfe.Smpp.Server.Networking;

/// <summary>
///     A received SMPP frame. <see cref="Pdu" /> is populated when the command id is recognised by the
///     reused PDU library; otherwise only <see cref="Header" /> and <see cref="Body" /> are available so the
///     session can reply appropriately (e.g. SMPP v5.0 broadcast operations or a <c>generic_nack</c>).
/// </summary>
/// <param name="Header">The always-parsed 16-byte PDU header.</param>
/// <param name="Pdu">The fully parsed PDU, or <c>null</c> for an unrecognised command id.</param>
/// <param name="Body">The raw PDU body bytes following the header.</param>
public readonly record struct SmppFrame(PDUHeader Header, PDU? Pdu, byte[] Body);
