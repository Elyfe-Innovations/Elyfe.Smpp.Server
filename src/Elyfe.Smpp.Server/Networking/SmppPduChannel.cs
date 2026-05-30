using JamaaTech.Smpp.Net.Lib;
using JamaaTech.Smpp.Net.Lib.Protocol;
using JamaaTech.Smpp.Net.Lib.Util;

namespace Elyfe.Smpp.Server.Networking;

/// <summary>
///     Reads and writes SMPP PDUs over a duplex <see cref="Stream" /> (plain TCP or TLS).
///     Reuses the <c>JamaaTech.Smpp.Net.Lib</c> PDU types for parsing and serialisation.
/// </summary>
public sealed class SmppPduChannel
{
    private const int HeaderLength = 16;
    private const int MaxPduLength = 1024 * 1024; // 1 MiB guard against malformed length fields.

    private readonly Stream _stream;
    private readonly SmppEncodingService _encoding;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    ///     Creates a channel over the supplied stream using the given encoding service.
    /// </summary>
    public SmppPduChannel(Stream stream, SmppEncodingService encoding)
    {
        _stream = stream;
        _encoding = encoding;
    }

    /// <summary>
    ///     Reads the next complete SMPP frame from the stream.
    /// </summary>
    /// <returns>
    ///     The parsed <see cref="SmppFrame" />, or <c>null</c> when the peer closed the connection. The frame's
    ///     <see cref="SmppFrame.Pdu" /> is <c>null</c> when the command id is not recognised by the PDU library.
    /// </returns>
    public async Task<SmppFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        if (!await ReadExactAsync(header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var commandLength = (int)_encoding.GetIntFromBytes(header[..4]);
        if (commandLength < HeaderLength || commandLength > MaxPduLength)
        {
            throw new InvalidDataException($"Invalid SMPP command_length {commandLength}.");
        }

        var bodyLength = commandLength - HeaderLength;
        var body = bodyLength > 0 ? new byte[bodyLength] : [];
        if (bodyLength > 0 && !await ReadExactAsync(body, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var headerBuffer = new ByteBuffer(header);
        var pduHeader = PDUHeader.Parse(headerBuffer, _encoding);

        PDU? pdu = null;
        try
        {
            pdu = PDU.CreatePDU(pduHeader, _encoding);
            if (bodyLength > 0)
            {
                pdu.SetBodyData(new ByteBuffer(body));
            }
        }
        catch (InvalidPDUCommandException)
        {
            // Unrecognised command id (e.g. SMPP v5.0 broadcast_sm). The session handles it from the header.
            pdu = null;
        }

        return new SmppFrame(pduHeader, pdu, body);
    }

    /// <summary>
    ///     Writes raw, pre-serialised bytes to the stream (used for manually built responses such as the
    ///     SMPP v5.0 broadcast operations not modelled by the PDU library).
    /// </summary>
    public async Task WriteRawAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Serialises and writes a PDU to the stream. Writes are serialised to avoid interleaving.
    /// </summary>
    public async Task WriteAsync(PDU pdu, CancellationToken cancellationToken)
    {
        var bytes = pdu.GetBytes();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false; // Peer closed the connection.
            }

            offset += read;
        }

        return true;
    }
}
