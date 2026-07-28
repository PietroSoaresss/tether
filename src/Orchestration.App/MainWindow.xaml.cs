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

    /// <summary>Set while LoadWorkspace materializes the saved graph, so it does not look like an edit.</summary>
    private bool _loading;

    /// <summary>
    /// Set when the workspace could not be read at all. Something is on disk that we failed to open
    /// or parse, and an empty in-memory model must never be written over it — not on a timer, and
    /// not at close either.
    /// </summary>
    private bool _saveSuppressed;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Orchestration";

        _autosave = new Autosave(SaveWorkspace, TimeSpan.FromSeconds(1));

        LoadWorkspace();

        Closed += (_, _) =>
        {
            // Dispose first so no timer can fire into a half-torn-down window, then save
            // unconditionally. FlushNow would consult _pending, which Fire() clears before it
            // marshals the save onto a dispatcher queue that stops pumping the moment we close —
            // a race that silently drops the user's last second of work.
            _autosave.Dispose();
            SaveNow();
            foreach (var node in _nodes)
                if (node.Node is TerminalNodeView terminal) terminal.DisposeSession();
        };
    }

    private void LoadWorkspace()
    {
        _workspace = _store.Load();

        _offsetX = _workspace.Camera.OffsetX;
        _offsetY = _workspace.Camera.OffsetY;
        // WorkspaceStore already clamped this into Camera's range; clamping again here is how the
        // same invariant ends up half-enforced in two places.
        _zoom = _workspace.Camera.Zoom;

        // Restoring the saved graph is not an edit. AddNode schedules an autosave, so without this
        // guard every launch writes one second later with no user input — and after a recovery from
        // .bak that write rotates the corrupt primary into the backup slot, destroying the only
        // good copy while the user is still reading the warning.
        _loading = true;
        try
        {
            // Materialize appends to _workspace.Nodes, so iterate a snapshot and start from empty.
            var saved = _workspace.Nodes.ToList();
            _workspace.Nodes.Clear();
            foreach (var model in saved) Materialize(model);
        }
        finally
        {
            _loading = false;
        }

        UpdateZoomLabel();

        if (_store.LastLoadOutcome == ReadOutcome.Backup)
            ShowRecoveryNotice("O workspace principal estava corrompido. Recuperado a partir do backup.");

        // Unreadable means a file is there that we could not open or parse — a lock from antivirus,
        // a sync client or a second instance. Seeding would schedule a write over content that is
        // probably intact, so we seed nothing and refuse to save for the rest of the session.
        if (_store.LastLoadOutcome == ReadOutcome.Unreadable)
        {
            _saveSuppressed = true;
            ShowRecoveryNotice(
                "Nao foi possivel ler o workspace salvo. Nada sera gravado sobre ele nesta sessao; " +
                "feche o programa e tente de novo depois de liberar o arquivo.");
            return;
        }

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
        // Autosave fires on a timer thread, so the model has to be read on the UI thread. The
        // close path calls SaveNow directly instead of coming through here, because by then the
        // queue no longer pumps and an enqueued save would never run.
        if (DispatcherQueue.HasThreadAccess) SaveNow();
        else DispatcherQueue.TryEnqueue(SaveNow);
    }

    private void SaveNow()
    {
        if (_saveSuppressed) return;

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
