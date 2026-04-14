using System.IO;
using System.Text.Json;

namespace Btl.Whiteboard.Contracts;

public sealed record WhiteboardEnvelope(
    WhiteboardMessageType Type,
    string SenderId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    JsonElement Payload)
{
    public static WhiteboardEnvelope Create<TPayload>(
        WhiteboardMessageType type,
        string senderId,
        long sequence,
        TPayload payload)
    {
        if (string.IsNullOrWhiteSpace(senderId))
        {
            throw new ArgumentException("SenderId cannot be null or empty.", nameof(senderId));
        }

        ArgumentNullException.ThrowIfNull(payload);

        return new WhiteboardEnvelope(
            type,
            senderId,
            sequence,
            DateTimeOffset.UtcNow,
            ProtocolJson.ToJsonElement(payload));
    }

    public TPayload ReadPayload<TPayload>()
    {
        if (Payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Payload for {Type} is empty.");
        }

        try
        {
            TPayload? parsed = ProtocolJson.Deserialize<TPayload>(Payload);
            if (parsed is null)
            {
                throw new InvalidDataException(
                    $"Unable to parse payload for {Type} as {typeof(TPayload).Name}. PayloadPreview={BuildPayloadPreview(Payload.GetRawText())}");
            }

            return parsed;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Failed to deserialize payload for {Type} as {typeof(TPayload).Name}. PayloadPreview={BuildPayloadPreview(Payload.GetRawText())}",
                exception);
        }
    }

    private static string BuildPayloadPreview(string rawPayload)
    {
        const int maxPreviewLength = 320;
        string compact = rawPayload
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (compact.Length <= maxPreviewLength)
        {
            return compact;
        }

        return compact[..maxPreviewLength] + "...";
    }
}
