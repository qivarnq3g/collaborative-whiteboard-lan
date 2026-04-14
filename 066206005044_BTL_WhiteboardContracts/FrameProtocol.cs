using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Btl.Whiteboard.Contracts;

public static class FrameProtocol
{
    private const int MinMessageBytes = 2;
    private const int MaxMessageBytes = 2_097_152;

    public static async Task WriteAsync(
        Stream stream,
        WhiteboardEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new InvalidOperationException("Target stream is not writable.");
        }

        string json = JsonSerializer.Serialize(envelope, ProtocolJson.SerializerOptions);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(json);
        if (payloadBytes.Length < MinMessageBytes || payloadBytes.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"Payload length must be within [{MinMessageBytes}, {MaxMessageBytes}] bytes.");
        }

        byte[] headerBuffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer, payloadBytes.Length);

        await stream.WriteAsync(headerBuffer, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WhiteboardEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("Source stream is not readable.");
        }

        byte[] headerBuffer = new byte[sizeof(int)];
        bool headerRead = await TryReadExactAsync(stream, headerBuffer, cancellationToken).ConfigureAwait(false);
        if (!headerRead)
        {
            return null;
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer);
        if (payloadLength < MinMessageBytes || payloadLength > MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Invalid payload length: {payloadLength}. HeaderHex={Convert.ToHexString(headerBuffer)}. " +
                $"Expected range [{MinMessageBytes}, {MaxMessageBytes}] bytes.");
        }

        byte[] payloadBuffer = new byte[payloadLength];
        bool payloadRead = await TryReadExactAsync(stream, payloadBuffer, cancellationToken).ConfigureAwait(false);
        if (!payloadRead)
        {
            return null;
        }

        WhiteboardEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WhiteboardEnvelope>(
                payloadBuffer,
                ProtocolJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Unable to deserialize whiteboard message envelope. PayloadPreview={BuildPayloadPreview(payloadBuffer)}",
                exception);
        }

        if (envelope is null)
        {
            throw new InvalidDataException($"Unable to deserialize whiteboard message envelope. PayloadPreview={BuildPayloadPreview(payloadBuffer)}");
        }

        return envelope;
    }

    private static string BuildPayloadPreview(byte[] payloadBuffer)
    {
        const int maxBytes = 320;
        int bytesToDecode = Math.Min(maxBytes, payloadBuffer.Length);

        string preview = Encoding.UTF8.GetString(payloadBuffer, 0, bytesToDecode)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (bytesToDecode < payloadBuffer.Length)
        {
            preview += "...";
        }

        return preview;
    }

    private static async Task<bool> TryReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
