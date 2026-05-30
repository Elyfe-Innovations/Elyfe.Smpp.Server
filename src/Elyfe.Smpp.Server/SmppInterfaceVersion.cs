namespace Elyfe.Smpp.Server;

/// <summary>
///     Supported SMPP interface (protocol) versions negotiated at bind time via the
///     <c>interface_version</c> field.
/// </summary>
public enum SmppInterfaceVersion : byte
{
    /// <summary>SMPP protocol version 3.3 (legacy; treated as 3.4 capable).</summary>
    V33 = 0x33,

    /// <summary>SMPP protocol version 3.4.</summary>
    V34 = 0x34,

    /// <summary>SMPP protocol version 5.0.</summary>
    V50 = 0x50
}
