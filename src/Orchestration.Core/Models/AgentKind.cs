namespace Orchestration.Core.Models;

/// <summary>
/// The one place that knows which terminal kinds exist. Command line, label, badge and glyph travel
/// together so adding an agent is one entry instead of four edits that can drift apart.
/// The glyph is a font codepoint string, not a UI type, so this stays usable from Core.
/// </summary>
public sealed record AgentKind(string Id, string Label, string Badge, string CommandLine, string Glyph)
{
    /// <summary>
    /// Launched through the shell rather than directly, because CreateProcess with a null
    /// application name only finds .exe. On this machine `claude` happens to be claude.exe, but
    /// `codex` is codex.ps1 - a script CreateProcess cannot run at all, and npm .cmd shims are
    /// just as common. The shell resolves all three, and -NoExit keeps the prompt open so a
    /// missing CLI reports itself inside the terminal instead of vanishing.
    /// </summary>
    private static string Through(string cli) => $"powershell.exe -NoLogo -NoExit -Command {cli}";

    // Glyphs are Segoe Fluent Icons codepoints: CommandPrompt, Robot, Code, Brightness.
    public static readonly AgentKind PowerShell =
        new("powershell", "PowerShell", "TERMINAL", "powershell.exe -NoLogo", "");

    public static readonly AgentKind Claude =
        new("claude", "Claude", "CLAUDE", Through("claude"), "");

    public static readonly AgentKind Codex =
        new("codex", "Codex", "CODEX", Through("codex"), "");

    public static readonly AgentKind Gemini =
        new("gemini", "Gemini", "GEMINI", Through("gemini"), "");

    public static readonly IReadOnlyList<AgentKind> All = new[] { PowerShell, Claude, Codex, Gemini };

    /// <summary>Unknown ids fall back to a plain shell rather than throwing at load time.</summary>
    public static AgentKind Find(string? id) =>
        All.FirstOrDefault(kind => string.Equals(kind.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? PowerShell;

    /// <summary>
    /// Recovers the kind of a terminal saved before Kind existed. PowerShell has to be excluded from
    /// the scan rather than merely ordered last: every agent command line is itself wrapped in
    /// powershell.exe, so it would match all of them.
    /// </summary>
    public static AgentKind FromCommandLine(string? commandLine)
    {
        string command = commandLine?.ToLowerInvariant() ?? "";
        foreach (var kind in All.Where(k => k != PowerShell))
            if (command.Contains(kind.Id, StringComparison.Ordinal))
                return kind;
        return PowerShell;
    }

    /// <summary>Agents a terminal may start through `tether spawn`. A bare shell is not one.</summary>
    public static bool CanSpawn(string? id) =>
        All.Any(kind => kind != PowerShell && string.Equals(kind.Id, id, StringComparison.OrdinalIgnoreCase));
}
