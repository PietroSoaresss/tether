using Microsoft.UI.Xaml;
using Orchestration.App.Views;
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;

namespace Orchestration.App;

public sealed partial class MainWindow : Window
{
    private readonly WorkspaceStore _store = new(new TetherPaths());
    private readonly Autosave _autosave;
    private Workspace _workspace = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "Orchestration";

        _autosave = new Autosave(SaveWorkspace, TimeSpan.FromSeconds(1));

        LoadWorkspace();

        Closed += (_, _) =>
        {
            _autosave.FlushNow();
            _autosave.Dispose();
            foreach (var node in _nodes)
                if (node.Node is TerminalNodeView terminal) terminal.DisposeSession();
        };
    }

    private void LoadWorkspace()
    {
        _workspace = _store.Load();

        _offsetX = _workspace.Camera.OffsetX;
        _offsetY = _workspace.Camera.OffsetY;
        // A hand-edited file can carry a zero zoom, and both the spawn point and the drag delta
        // divide by it.
        _zoom = Math.Clamp(_workspace.Camera.Zoom, MinZoom, MaxZoom);

        // Materialize appends to _workspace.Nodes, so iterate a snapshot and start from empty.
        var saved = _workspace.Nodes.ToList();
        _workspace.Nodes.Clear();
        foreach (var model in saved) Materialize(model);

        UpdateZoomLabel();

        if (_store.LastLoadOutcome == ReadOutcome.Backup)
            ShowRecoveryNotice("O workspace principal estava corrompido. Recuperado a partir do backup.");

        // A first run has nothing to show, and an empty canvas teaches nothing. Keyed off the read
        // outcome rather than the node count: a user who deleted every node meant it, and seeding
        // on count alone would resurrect nodes on every launch and overwrite their empty workspace.
        if (_store.LastLoadOutcome == ReadOutcome.None)
        {
            OnNewTerminal(this, new RoutedEventArgs());
            OnNewNote(this, new RoutedEventArgs());
        }
    }

    private void SaveWorkspace()
    {
        // Autosave fires on a timer thread, so the model has to be read on the UI thread. But at
        // window close FlushNow() calls this from the Closed handler, already on the UI thread,
        // and the queue never pumps again — enqueueing there would drop the user's last edits.
        if (DispatcherQueue.HasThreadAccess) SaveNow();
        else DispatcherQueue.TryEnqueue(SaveNow);
    }

    private void SaveNow()
    {
        try
        {
            _store.Save(_workspace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A transient lock from antivirus or a sync client must not take the process down
            // on a one-second timer. Losing one write costs a second; crashing costs the session.
            ShowRecoveryNotice($"Nao foi possivel salvar o workspace: {e.Message}");
        }
    }

    private void ShowRecoveryNotice(string message)
    {
        RecoveryBar.Message = message;
        RecoveryBar.IsOpen = true;
    }
}
