using Orchestration.Core.Models;

namespace Orchestration.App.Services;

public static class AgentPrimer
{
    private const string Start = "<!-- tether:start -->";
    private const string End = "<!-- tether:end -->";

    public static void Write(string workingDirectory, TerminalNode node, IEnumerable<NodeBase> neighbors)
    {
        if (!Directory.Exists(workingDirectory)) return;

        string list = string.Join(
            Environment.NewLine,
            neighbors.Select(neighbor => $"- {neighbor.Id}  {NodeKinds.Label(neighbor)}  {neighbor.Title}"));
        string block =
            $"{Start}{Environment.NewLine}" +
            "Este terminal roda dentro do Tether. Antes de dizer que nao consegue acessar outro no, execute `tether list`." + Environment.NewLine +
            "Ferramentas disponiveis:" + Environment.NewLine +
            "- tether list" + Environment.NewLine +
            "- tether ask <no> <prompt>" + Environment.NewLine +
            "- tether spawn claude|codex --title \"<titulo>\" --prompt \"<tarefa>\"" + Environment.NewLine +
            "Agentes criados com `tether spawn` abrem na mesma pasta de projeto deste terminal." + Environment.NewLine +
            "- tether note show <nome>" + Environment.NewLine +
            "- \"conteudo\" | tether note edit <nome>" + Environment.NewLine +
            "- \"conteudo adicional\" | tether note edit <nome> --append" + Environment.NewLine +
            "Os cabos podem mudar durante a sessao; `tether list` e sempre a fonte atual. Vizinhos no inicio:" + Environment.NewLine +
            (string.IsNullOrEmpty(list) ? "- nenhum" : list) + Environment.NewLine +
            $"{End}";

        Upsert(Path.Combine(workingDirectory, "AGENTS.md"), block, create: true);
        bool isClaude = node.CommandLine.Contains("claude", StringComparison.OrdinalIgnoreCase);
        Upsert(Path.Combine(workingDirectory, "CLAUDE.md"), block, create: isClaude);
    }

    private static void Upsert(string path, string block, bool create)
    {
        if (!create && !File.Exists(path)) return;
        string current = File.Exists(path) ? File.ReadAllText(path) : "";
        int start = current.IndexOf(Start, StringComparison.Ordinal);
        int end = current.IndexOf(End, StringComparison.Ordinal);
        string updated = start >= 0 && end >= start
            ? current[..start] + block + current[(end + End.Length)..]
            : string.IsNullOrWhiteSpace(current) ? block + Environment.NewLine : current.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine;
        string temporary = path + ".tether.tmp";
        File.WriteAllText(temporary, updated);
        File.Move(temporary, path, overwrite: true);
    }
}
