using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tether-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TetherPaths _paths;

    public SettingsStoreTests() => _paths = new TetherPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        var settings = new SettingsStore(_paths).Load();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(14, settings.TerminalFontSize);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new SettingsStore(_paths);
        store.Save(new AppSettings
        {
            Theme = AppTheme.Dark,
            TerminalFontSize = 18,
            IdleMs = 900,
            Shortcuts = { ["novo-terminal"] = "Ctrl+T" }
        });

        var loaded = store.Load();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(18, loaded.TerminalFontSize);
        Assert.Equal(900, loaded.IdleMs);
        Assert.Equal("Ctrl+T", loaded.Shortcuts["novo-terminal"]);
    }

    [Fact]
    public void Load_WithACorruptFile_FallsBackToDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.SettingsFile, "nao e json");

        var settings = new SettingsStore(_paths).Load();

        Assert.Equal(AppTheme.System, settings.Theme);
    }
}
