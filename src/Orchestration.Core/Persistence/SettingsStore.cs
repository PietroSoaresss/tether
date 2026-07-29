using System.Text.Json;
using Orchestration.Core.Models;

namespace Orchestration.Core.Persistence;

/// <summary>
/// Settings live in their own file because they change on a completely different rhythm from
/// the workspace. A broken settings file falls back to defaults: it must never stop the app
/// from opening.
/// </summary>
public sealed class SettingsStore
{
    private readonly TetherPaths _paths;

    public SettingsStore(TetherPaths paths) => _paths = paths;

    public AppSettings Load()
    {
        AtomicFile.TryRead<AppSettings>(
            _paths.SettingsFile,
            json => JsonSerializer.Deserialize<AppSettings>(json, TetherJson.Options),
            out var settings);

        settings ??= new AppSettings();
        settings.Shortcuts ??= new Dictionary<string, string>();
        settings.RecentProjects ??= new List<string>();
        settings.RecentProjects = settings.RecentProjects
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.LastProjectDirectory ??= "";
        if (string.IsNullOrWhiteSpace(settings.TerminalFontFamily))
            settings.TerminalFontFamily = new AppSettings().TerminalFontFamily;
        settings.TerminalFontSize = Math.Clamp(settings.TerminalFontSize, 8, 32);
        settings.IdleMs = Math.Clamp(settings.IdleMs, 200, 30_000);
        settings.AskTimeoutMs = Math.Clamp(settings.AskTimeoutMs, 1_000, 600_000);
        settings.MaxCallDepth = Math.Clamp(settings.MaxCallDepth, 1, 20);
        return settings;
    }

    public void Save(AppSettings settings) =>
        AtomicFile.Write(_paths.SettingsFile, JsonSerializer.Serialize(settings, TetherJson.Options));
}
