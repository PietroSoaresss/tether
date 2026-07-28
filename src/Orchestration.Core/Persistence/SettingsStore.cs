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

        return settings ?? new AppSettings();
    }

    public void Save(AppSettings settings) =>
        AtomicFile.Write(_paths.SettingsFile, JsonSerializer.Serialize(settings, TetherJson.Options));
}
