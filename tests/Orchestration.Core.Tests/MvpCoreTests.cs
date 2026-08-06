using Orchestration.Core.Graph;
using Orchestration.Core.Ipc;
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Orchestration.Core.Terminal;
using System.Text.Json;

namespace Orchestration.Core.Tests;

public class MvpCoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tether-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Authorization_UsesDirectionAndTreatsNotesAsUndirected()
    {
        var a = new TerminalNode { Title = "a" };
        var b = new TerminalNode { Title = "b" };
        var note = new NoteNode { Title = "nota" };
        var canvas = new CanvasTab
        {
            Nodes = { a, b, note },
            Connections =
            {
                new Connection { SourceId = a.Id, TargetId = b.Id },
                new Connection { SourceId = note.Id, TargetId = b.Id }
            }
        };

        Assert.True(Authorization.CanAccess(canvas, a.Id, b.Id));
        Assert.False(Authorization.CanAccess(canvas, b.Id, a.Id));
        Assert.True(Authorization.CanAccess(canvas, b.Id, note.Id));
        Assert.False(CallChain.CanEnter(new[] { a.Id }, a.Id, 5));
    }

    [Fact]
    public void NoteFiles_CreateSafeUniqueNamesAndRejectTraversal()
    {
        var notes = new NoteFiles(new TetherPaths(_root));
        string first = notes.CreateUniqueName("Reunião Geral");
        notes.Write(first, "# um");
        string second = notes.CreateUniqueName("Reunião Geral");

        Assert.Equal("reuniao-geral.md", first);
        Assert.Equal("reuniao-geral-2.md", second);
        Assert.Equal("# um", notes.Read(first));
        Assert.Throws<ArgumentException>(() => notes.Resolve(@"..\fora.md"));
        Assert.Throws<ArgumentException>(() => notes.Resolve("nota.txt"));

        string projectFolder = Path.Combine(_root, "project", "notes");
        string projectNote = notes.CreateUniqueName("Plano", projectFolder);
        notes.Write(projectNote, "# plano", projectFolder);
        notes.Write(projectNote, "# plano atualizado", projectFolder);
        Assert.Equal("# plano atualizado", File.ReadAllText(Path.Combine(projectFolder, "plano.md")));
        Assert.False(File.Exists(Path.Combine(projectFolder, "plano.md.bak")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "note-backups")));
    }

    [Fact]
    public void EnvironmentBlock_OverridesPathAndEndsWithTwoNulls()
    {
        string block = EnvironmentBlock.Build(new Dictionary<string, string>
        {
            ["PATH"] = "novo",
            ["TETHER_PIPE"] = PipeNaming.ForProcess(42)
        });

        Assert.Contains("PATH=novo\0", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TETHER_PIPE=tether-42\0", block);
        Assert.EndsWith("\0\0", block);
    }

    [Fact]
    public void IpcJson_StaysOnOneLine()
    {
        string json = JsonSerializer.Serialize(
            new TetherRequest { Cmd = "list", From = Guid.NewGuid() },
            IpcJson.Options);

        Assert.DoesNotContain('\n', json);
    }

    [Fact]
    public void SpawnCommand_ParsesOnlySupportedAgents()
    {
        Assert.True(TetherCommandParser.TryParseSpawn(
            ["spawn", "claude", "--title", "Revisor", "--prompt", "Revise os testes"],
            out var values,
            out _));
        Assert.Equal("claude", values["kind"]);
        Assert.Equal("Revisor", values["title"]);
        Assert.Equal("Revise os testes", values["prompt"]);
        Assert.False(TetherCommandParser.TryParseSpawn(
            ["spawn", "powershell", "--prompt", "teste"],
            out _,
            out _));
    }

    [Fact]
    public void SettingsStore_NormalizesUnsafeValues()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "settings.json"),
            """{"TerminalFontFamily":"","TerminalFontSize":0,"IdleMs":0,"AskTimeoutMs":0,"MaxCallDepth":0,"Shortcuts":null}""");

        var settings = new SettingsStore(new TetherPaths(_root)).Load();

        Assert.Equal(8, settings.TerminalFontSize);
        Assert.Equal(200, settings.IdleMs);
        Assert.NotNull(settings.Shortcuts);
    }
}
