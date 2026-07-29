using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Orchestration.Core.Ipc;

namespace Orchestration.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? pipeName = Environment.GetEnvironmentVariable("TETHER_PIPE");
        string? nodeText = Environment.GetEnvironmentVariable("TETHER_NODE");
        if (string.IsNullOrWhiteSpace(pipeName) || !Guid.TryParse(nodeText, out var from))
            return Error("tether nao esta rodando dentro de um no do Tether");
        if (args.Length == 0)
            return Error("uso: tether list | ask <no> <prompt> | spawn claude|codex --prompt <tarefa> | note show|edit <nota>");

        var request = new TetherRequest { From = from };
        switch (args[0].ToLowerInvariant())
        {
            case "list":
                request.Cmd = "list";
                break;
            case "ask" when args.Length >= 3:
                request.Cmd = "ask";
                request.Args["target"] = args[1];
                request.Args["prompt"] = string.Join(' ', args.Skip(2));
                break;
            case "spawn":
                if (!TetherCommandParser.TryParseSpawn(args, out var spawn, out string error))
                    return Error(error);
                request.Cmd = "spawn";
                request.Args = spawn;
                break;
            case "note" when args.Length >= 3 && args[1] == "show":
                request.Cmd = "note-show";
                request.Args["target"] = args[2];
                break;
            case "note" when args.Length >= 3 && args[1] == "edit":
                request.Cmd = "note-edit";
                request.Args["target"] = args[2];
                request.Args["append"] = args.Contains("--append") ? "true" : "false";
                request.Args["content"] = await Console.In.ReadToEndAsync();
                break;
            default:
                return Error("comando invalido");
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, IpcJson.Options));
            string? line = await reader.ReadLineAsync();
            var response = line is null
                ? null
                : JsonSerializer.Deserialize<TetherResponse>(line, IpcJson.Options);
            if (response is null) return Error("resposta invalida do Tether");

            if (!string.IsNullOrEmpty(response.Data)) Console.Out.Write(response.Data);
            if (response.Ok) return 0;
            if (!string.IsNullOrEmpty(response.Error)) Console.Error.WriteLine(response.Error);
            return response.Error is "timeout" or "target exited" ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            return Error($"nao foi possivel conectar ao Tether: {e.Message}");
        }
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
