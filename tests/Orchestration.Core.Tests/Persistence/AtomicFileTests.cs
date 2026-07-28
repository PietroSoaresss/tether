using System.Text.Json;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tether-tests-" + Guid.NewGuid().ToString("N"));

    private string Path0 => Path.Combine(_root, "sub", "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Box { public string Value { get; set; } = ""; }

    private static Box? Parse(string json) => JsonSerializer.Deserialize<Box>(json, TetherJson.Options);

    [Fact]
    public void Write_CreatesMissingDirectories()
    {
        AtomicFile.Write(Path0, "{}");
        Assert.True(File.Exists(Path0));
    }

    [Fact]
    public void Write_LeavesNoTemporaryFileBehind()
    {
        AtomicFile.Write(Path0, "{}");
        AtomicFile.Write(Path0, "{}");
        Assert.False(File.Exists(Path0 + ".tmp"));
    }

    [Fact]
    public void Write_KeepsThePreviousContentAsBackup()
    {
        AtomicFile.Write(Path0, "{\"Value\":\"primeiro\"}");
        AtomicFile.Write(Path0, "{\"Value\":\"segundo\"}");

        Assert.Contains("segundo", File.ReadAllText(Path0));
        Assert.Contains("primeiro", File.ReadAllText(Path0 + ".bak"));
    }

    [Fact]
    public void TryRead_ReadsThePrimaryFile()
    {
        AtomicFile.Write(Path0, "{\"Value\":\"ok\"}");

        var outcome = AtomicFile.TryRead<Box>(Path0, Parse, out var box);

        Assert.Equal(ReadOutcome.Primary, outcome);
        Assert.Equal("ok", box!.Value);
    }

    [Fact]
    public void TryRead_FallsBackToTheBackupWhenThePrimaryIsCorrupt()
    {
        AtomicFile.Write(Path0, "{\"Value\":\"bom\"}");
        AtomicFile.Write(Path0, "{\"Value\":\"tambem bom\"}");

        // Simulate a machine that died mid-write: valid .bak, garbage primary.
        File.WriteAllText(Path0, "{ isto nao e json");

        var outcome = AtomicFile.TryRead<Box>(Path0, Parse, out var box);

        Assert.Equal(ReadOutcome.Backup, outcome);
        Assert.Equal("bom", box!.Value);
    }

    [Fact]
    public void TryRead_ReturnsNoneWhenNothingIsUsable()
    {
        var outcome = AtomicFile.TryRead<Box>(Path0, Parse, out var box);

        Assert.Equal(ReadOutcome.None, outcome);
        Assert.Null(box);
    }

    [Fact]
    public void TryRead_ReturnsNoneWhenEverythingIsCorrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path0)!);
        File.WriteAllText(Path0, "lixo");
        File.WriteAllText(Path0 + ".bak", "lixo tambem");

        Assert.Equal(ReadOutcome.None, AtomicFile.TryRead<Box>(Path0, Parse, out _));
    }
}
