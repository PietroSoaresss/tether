using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestration.Core.Persistence;

/// <summary>
/// One serializer configuration for the whole product — persisted files and the IPC protocol.
/// Two configurations that drift apart is how a workspace becomes unreadable by its own app.
/// </summary>
public static class TetherJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
