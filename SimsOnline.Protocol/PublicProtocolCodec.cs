using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>
/// JSON encoder/decoder for the P3 public client-facing protocol.
///
/// Wire format: each message is a single newline-terminated UTF-8 JSON object.
/// Polymorphism uses System.Text.Json's "$type" discriminator with stable lowercase
/// snake-case names (e.g. "join_session", "session_joined") so the wire surface is
/// independent of internal C# type names.
///
/// Clients send <see cref="ClientCommand"/> subtypes; the server pushes
/// <see cref="ServerEvent"/> subtypes back over the same connection.
/// </summary>
public static class PublicProtocolCodec
{
    public static readonly JsonSerializerOptions JsonOptions = BuildOptions();

    public static string EncodeCommand(ClientCommand command) =>
        JsonSerializer.Serialize(command, JsonOptions);

    public static string EncodeEvent(ServerEvent serverEvent) =>
        JsonSerializer.Serialize(serverEvent, JsonOptions);

    public static ClientCommand DecodeCommand(string json) =>
        JsonSerializer.Deserialize<ClientCommand>(json, JsonOptions)
            ?? throw new FormatException("Empty or null client command JSON.");

    public static ServerEvent DecodeEvent(string json) =>
        JsonSerializer.Deserialize<ServerEvent>(json, JsonOptions)
            ?? throw new FormatException("Empty or null server event JSON.");

    public static bool TryDecodeCommand(string json, out ClientCommand? command, out string? error)
    {
        try
        {
            command = JsonSerializer.Deserialize<ClientCommand>(json, JsonOptions);
            error = command == null ? "Empty or null client command JSON." : null;
            return command != null;
        }
        catch (JsonException ex)
        {
            command = null;
            error = ex.Message;
            return false;
        }
        catch (NotSupportedException ex)
        {
            command = null;
            error = ex.Message;
            return false;
        }
    }

    private static JsonSerializerOptions BuildOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
