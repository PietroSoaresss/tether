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
        _zoom = _workspace.Camera.Zoom;

        // Materialize appends to _workspace.Nodes, so iterate a snapshot and start from empty.
        var saved = _workspace.Nodes.ToList();
        _workspace.Nodes.Clear();
        foreach (var model in saved) Materialize(model);

        UpdateZoomLabel();

        if (_store.LastLoadOutcome == ReadOutcome.Backup)
            ShowRecoveryNotice("O workspace principal estava corrompido. Recuperado a partir do backup.");

        // A first run has nothing to show, and an empty canvas teaches nothing.
        if (saved.Count == 0)
        {
            OnNewTerminal(this, new RoutedEventArgs());
            OnNewNote(this, new RoutedEventArgs());
        }
    }

    private void SaveWorkspace()
    {
        // Autosave fires on a timer thread; the model is only safe to read on the UI thread.
        DispatcherQueue.TryEnqueue(() => _store.Save(_workspace));
    }

    private void ShowRecoveryNotice(string message)
    {
        RecoveryBar.Message = message;
        RecoveryBar.IsOpen = true;
    }
}
