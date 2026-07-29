using System.Text.Json;
using Orchestration.Core.Persistence;

namespace Orchestration.Core.Ipc;

public sealed class TetherRequest
{
    public string Cmd { get; set; } = "";
    public Guid From { get; set; }
    public Dictionary<string, string> Args { get; set; } = new();
    public List<Guid> Chain { get; set; } = new();
}

public sealed class TetherResponse
{
    public bool Ok { get; set; }
    public string Data { get; set; } = "";
    public string? Error { get; set; }

    public static TetherResponse Success(string data = "") => new() { Ok = true, Data = data };
    public static TetherResponse Failure(string error, string data = "") =>
        new() { Error = error, Data = data };
}

public static class TetherCommandParser
{
    public static bool TryParseSpawn(
        IReadOnlyList<string> args,
        out Dictionary<string, string> values,
        out string error)
    {
        values = new();
        error = "uso: tether spawn claude|codex --title <titulo> --prompt <tarefa>";
        if (args.Count < 2 || !string.Equals(args[0], "spawn", StringComparison.OrdinalIgnoreCase))
            return false;

        string kind = args[1].ToLowerInvariant();
        if (kind is not ("claude" or "codex")) return false;
        values["kind"] = kind;

        for (int i = 2; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count || args[i] is not ("--title" or "--prompt") ||
                !values.TryAdd(args[i][2..], args[i + 1]))
                return false;
        }

        return values.TryGetValue("prompt", out string? prompt) &&
               !string.IsNullOrWhiteSpace(prompt);
    }
}

public static class PipeNaming
{
    public static string ForProcess(int processId) => $"tether-{processId}";
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(TetherJson.Options)
    {
        WriteIndented = false
    };
}
