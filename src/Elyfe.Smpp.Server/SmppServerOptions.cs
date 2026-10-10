namespace Elyfe.Smpp.Server;

/// <summary>
///     Configuration options for the <see cref="SmppServer" />.
/// </summary>
public sealed class SmppServerOptions
{
    /// <summary>
    ///     The configuration section name used when binding from <c>appsettings.json</c>.
    /// </summary>
    public const string SectionName = "SmppServer";

    /// <summary>
    ///     The TCP port the server listens on. Defaults to the IANA-registered SMPP port 2775.
    /// </summary>
    public int Port { get; set; } = 2775;

    /// <summary>
    ///     The IP address to bind the listener to. Defaults to all interfaces (<c>0.0.0.0</c>).
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    ///     The system id advertised by the server in bind responses. Identifies this SMSC to ESMEs.
    /// </summary>
    public string SystemId { get; set; } = "Elyfe.Smpp.Server";

    /// <summary>
    ///     Maximum number of concurrent sessions allowed across the whole server. Zero or negative disables the cap.
    /// </summary>
    public int MaxSessions { get; set; } = 1000;

    /// <summary>
    ///     Maximum number of concurrent sessions allowed per <c>system_id</c>. Zero or negative disables the cap.
    /// </summary>
    public int MaxSessionsPerSystemId { get; set; } = 10;

    /// <summary>
    ///     Idle timeout. A session that neither sends nor receives a PDU within this window is closed.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long the server waits for a bind PDU after a TCP connection is accepted before closing it. The same
    ///     deadline applies to the TLS handshake when TLS is enabled.
    /// </summary>
    public TimeSpan BindTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The interval the server expects <c>enquire_link</c> keepalives within. Used together with
    ///     <see cref="IdleTimeout" /> to detect dead peers.
    /// </summary>
    public TimeSpan EnquireLinkInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Lowest SMPP interface version accepted at bind time. <c>0x34</c> = v3.4, <c>0x50</c> = v5.0.
    /// </summary>
    public byte MinInterfaceVersion { get; set; } = 0x34;

    /// <summary>
    ///     Highest SMPP interface version accepted at bind time. <c>0x34</c> = v3.4, <c>0x50</c> = v5.0.
    /// </summary>
    public byte MaxInterfaceVersion { get; set; } = 0x50;

    /// <summary>
    ///     SMPP version reported to ESMEs in the <c>sc_interface_version</c> TLV of a successful bind response.
    ///     Defaults to <c>0x34</c> because the v5.0-only operations (e.g. broadcast) are not implemented; v5.0 ESMEs
    ///     may still bind up to <see cref="MaxInterfaceVersion" />.
    /// </summary>
    public byte AdvertisedInterfaceVersion { get; set; } = 0x34;

    /// <summary>
    ///     Per-session inbound submit rate limit (messages/second). Zero or negative disables throttling.
    /// </summary>
    public int MaxSubmitsPerSecond { get; set; } = 100;

    /// <summary>
    ///     SMPP window size: maximum number of server-originated <c>deliver_sm</c> PDUs a session may have in flight
    ///     awaiting <c>deliver_sm_resp</c>. Further deliveries wait for a free slot. Zero or negative disables the
    ///     window check.
    /// </summary>
    public int WindowSize { get; set; } = 100;

    /// <summary>
    ///     How long a delivery waits for a window slot and the ESME's <c>deliver_sm_resp</c> before it is reported as
    ///     failed. Defaults to one day so a slow ESME gets the widest opportunity to acknowledge; a delivery still
    ///     fails sooner if the session closes or the caller cancels. Zero or negative waits until the session closes.
    /// </summary>
    public TimeSpan DeliverResponseTimeout { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    ///     How long shutdown waits for bound sessions to complete in-flight exchanges and acknowledge the server's
    ///     <c>unbind</c> before connections are closed. Zero or negative closes connections immediately.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     TLS configuration. When <see cref="TlsOptions.Enabled" /> is <c>true</c> the listener performs a TLS
    ///     handshake before any SMPP traffic.
    /// </summary>
    public TlsOptions Tls { get; set; } = new();
}

/// <summary>
///     TLS configuration for the SMPP listener (SMPP-over-TLS).
/// </summary>
public sealed class TlsOptions
{
    /// <summary>
    ///     Whether TLS is required for incoming connections.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Path to a PKCS#12 (.pfx) certificate file used for the server-side TLS handshake.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    ///     Optional password protecting <see cref="CertificatePath" />.
    /// </summary>
    public string? CertificatePassword { get; set; }
}
