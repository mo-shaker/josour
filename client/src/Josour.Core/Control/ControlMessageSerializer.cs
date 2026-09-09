using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Josour.Core.Control;

/// <summary>
/// A frame whose <c>type</c> this build does not know (newer server, or a typo on either side).
/// <see cref="RawJson"/> keeps the whole frame for logging; it is written back verbatim by the serializer.
/// </summary>
public sealed record UnknownControlMessage(string Type, [property: JsonIgnore] string RawJson) : ControlMessage(Type);

/// <summary>
/// System.Text.Json (de)serialization of <see cref="ControlMessage"/> frames by their <c>type</c> field, using exactly the
/// wire names of docs/ws-protocol.md. Field names come from the <c>JsonPropertyName</c> attributes in ControlMessages.cs;
/// times are written as ISO-8601 UTC with a <c>Z</c> suffix; nulls are omitted when writing.
/// </summary>
public static class ControlMessageSerializer
{
    public const string TypePropertyName = "type";

    private static readonly Dictionary<string, Type> WireTypes = new(StringComparer.Ordinal)
    {
        // client -> server
        ["hello"] = typeof(HelloMessage),
        ["host.available"] = typeof(HostAvailableMessage),
        ["request.create"] = typeof(RequestCreateMessage),
        ["request.cancel"] = typeof(RequestCancelMessage),
        ["request.accept"] = typeof(RequestAcceptMessage),
        ["request.reject"] = typeof(RequestRejectMessage),
        ["session.endpoint"] = typeof(SessionEndpointMessage),
        ["session.connected"] = typeof(SessionConnectedMessage),
        ["session.connect_failed"] = typeof(SessionConnectFailedMessage),
        ["session.stats"] = typeof(SessionStatsMessage),
        ["session.end"] = typeof(SessionEndMessage),
        ["ping"] = typeof(PingMessage),
        ["pong"] = typeof(PongMessage),

        // server -> client
        ["hello.ack"] = typeof(HelloAckMessage),
        ["hosts.snapshot"] = typeof(HostsMessage),
        ["hosts.update"] = typeof(HostsMessage),
        ["request.created"] = typeof(RequestCreatedMessage),
        ["request.incoming"] = typeof(RequestIncomingMessage),
        ["request.result"] = typeof(RequestResultMessage),
        ["request.expired"] = typeof(RequestExpiredMessage),
        ["session.created"] = typeof(SessionCreatedMessage),
        ["session.peer_endpoint"] = typeof(SessionPeerEndpointMessage),
        ["session.active"] = typeof(SessionActiveMessage),
        ["session.terminate"] = typeof(SessionTerminateMessage),
        ["allowlist.updated"] = typeof(AllowlistUpdatedMessage),
        ["error"] = typeof(ErrorMessage),
    };

    /// <summary>Options shared by every frame; safe to reuse for nested DTOs (candidates, hosts, settings).</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>All wire type names this build understands.</summary>
    public static IReadOnlyCollection<string> KnownTypes => WireTypes.Keys;

    public static bool IsKnownType(string wireType) => WireTypes.ContainsKey(wireType);

    /// <summary>Serializes one frame. <see cref="UnknownControlMessage"/> is written back verbatim.</summary>
    public static string Serialize(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is UnknownControlMessage unknown)
        {
            return unknown.RawJson;
        }

        return JsonSerializer.Serialize(message, message.GetType(), Options);
    }

    public static byte[] SerializeToUtf8Bytes(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is UnknownControlMessage unknown)
        {
            return System.Text.Encoding.UTF8.GetBytes(unknown.RawJson);
        }

        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Options);
    }

    /// <summary>
    /// Parses one frame. Throws <see cref="JsonException"/> when the text is not a JSON object with a string <c>type</c>;
    /// returns <see cref="UnknownControlMessage"/> for a well-formed frame of an unknown type.
    /// </summary>
    public static ControlMessage Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return Deserialize(document.RootElement, json);
    }

    public static ControlMessage Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        return Deserialize(document.RootElement, rawJson: null);
    }

    private static ControlMessage Deserialize(JsonElement root, string? rawJson)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A control message must be a JSON object.");
        }

        if (!root.TryGetProperty(TypePropertyName, out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"A control message must carry a string \"{TypePropertyName}\" field.");
        }

        var wireType = typeElement.GetString()!;
        if (!WireTypes.TryGetValue(wireType, out var clrType))
        {
            return new UnknownControlMessage(wireType, rawJson ?? root.GetRawText());
        }

        var message = (ControlMessage?)root.Deserialize(clrType, Options)
            ?? throw new JsonException($"Could not deserialize control message of type \"{wireType}\".");

        if (!string.Equals(message.Type, wireType, StringComparison.Ordinal))
        {
            // Only reachable if a record's constructor hard-codes a different type than the table above maps to.
            throw new JsonException($"Control message type mismatch: frame says \"{wireType}\", record says \"{message.Type}\".");
        }

        return message;
    }

    /// <summary>The <c>ref</c> of a request/reply frame, or <c>null</c> for frames without one. Used to correlate replies.</summary>
    public static string? GetRef(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message switch
        {
            RequestCreateMessage m => m.Ref,
            RequestCancelMessage m => m.Ref,
            RequestAcceptMessage m => m.Ref,
            RequestRejectMessage m => m.Ref,
            RequestCreatedMessage m => m.Ref,
            ErrorMessage m => m.Ref,
            _ => null,
        };
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { WriteTypeFirst } },
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new NullableUtcDateTimeOffsetConverter());
        return options;
    }

    /// <summary>Reflection puts a derived record's fields before the inherited <c>Type</c>; the contract's frames lead with <c>"type"</c>.</summary>
    private static void WriteTypeFirst(JsonTypeInfo typeInfo)
    {
        if (!typeof(ControlMessage).IsAssignableFrom(typeInfo.Type))
        {
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            if (property.Name == TypePropertyName)
            {
                property.Order = int.MinValue;
            }
        }
    }

    /// <summary>Writes <c>2026-09-04T10:15:30.123Z</c> (the contract's format); reads any ISO-8601 offset form.</summary>
    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        // Milliseconds, like the server (isoformat(timespec="milliseconds") + "Z").
        private const string WireFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected an ISO-8601 timestamp string.");
            }

            if (reader.TryGetDateTimeOffset(out var value))
            {
                return value;
            }

            var text = reader.GetString();
            return DateTimeOffset.Parse(text!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Format(value));

        internal static string Format(DateTimeOffset value) =>
            value.ToUniversalTime().ToString(WireFormat, CultureInfo.InvariantCulture);
    }

    private sealed class NullableUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
    {
        private readonly UtcDateTimeOffsetConverter _inner = new();

        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : _inner.Read(ref reader, typeof(DateTimeOffset), options);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(UtcDateTimeOffsetConverter.Format(value.Value));
            }
        }
    }
}
