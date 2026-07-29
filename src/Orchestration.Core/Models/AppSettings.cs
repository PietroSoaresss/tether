namespace Orchestration.Core.Models;

public enum AppTheme { System, Light, Dark }

/// <summary>Lives in its own file: settings and workspace change at completely different rhythms.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string TerminalFontFamily { get; set; } = "Cascadia Mono, Consolas, monospace";
    public double TerminalFontSize { get; set; } = 14;
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    public List<string> RecentProjects { get; set; } = new();
    public string LastProjectDirectory { get; set; } = "";

    /// <summary>Quiescence window that ends a turn, in milliseconds.</summary>
    public int IdleMs { get; set; } = 1500;

    /// <summary>Hard ceiling on a single `tether ask`, in milliseconds.</summary>
    public int AskTimeoutMs { get; set; } = 120_000;

    /// <summary>How deep a chain of agents calling agents may go before it is refused.</summary>
    public int MaxCallDepth { get; set; } = 5;

    /// <summary>Whether to seed the tether instruction block into AGENTS.md in a node's working directory.</summary>
    public bool SeedAgentInstructions { get; set; } = true;
}
