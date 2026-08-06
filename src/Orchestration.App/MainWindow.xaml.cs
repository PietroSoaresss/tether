using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orchestration.App.Services;
using Orchestration.App.Views;
using Orchestration.Core.Ipc;
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;

namespace Orchestration.App;

public sealed partial class MainWindow : Window
{
    private readonly TetherPaths _paths = new();
    private WorkspaceStore _store;
    private readonly NoteFiles _noteFiles;
    private readonly SettingsStore _settingsStore;
    private readonly TetherServer _tetherServer;
    private readonly string _pipeName;
    private AppSettings _settings;
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
        Title = "Tether";

        _store = new WorkspaceStore(_paths);
        _noteFiles = new NoteFiles(_paths);
        _settingsStore = new SettingsStore(_paths);
        _settings = _settingsStore.Load();
        _pipeName = PipeNaming.ForProcess(Environment.ProcessId);
        _tetherServer = new TetherServer(_pipeName, HandleTetherRequest);
        _autosave = new Autosave(SaveWorkspace, TimeSpan.FromSeconds(1));

        LoadWorkspace();
        ApplySettings();
        RootGrid.KeyDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            if (HandleCanvasToolShortcut(e.Key)) e.Handled = true;
            else if (e.Key == Windows.System.VirtualKey.Delete) DeleteSelection();
            else if (e.Key == Windows.System.VirtualKey.Escape &&
                     ConnectionModeToggle.IsChecked == true)
                ConnectionModeToggle.IsChecked = false;
            else if (e.Key == Windows.System.VirtualKey.Escape)
                SetCanvasTool("select");
        };

        Closed += (_, _) =>
        {
            // Dispose first so no timer can fire into a half-torn-down window, then save
            // unconditionally. FlushNow would consult _pending, which Fire() clears before it
            // marshals the save onto a dispatcher queue that stops pumping the moment we close —
            // a race that silently drops the user's last second of work.
            _autosave.Dispose();
            SaveNow();
            foreach (var watcher in _noteWatchers.Values) watcher.Dispose();
            _noteReload?.Cancel();
            _fileTreeWatcher?.Dispose();
            _fileTreeRefresh?.Cancel();
            _tetherServer.Dispose();
            foreach (var node in _nodes)
                if (node.Node is TerminalNodeView terminal) terminal.DisposeSession();
        };
    }

    private void LoadWorkspace()
    {
        var legacyStore = _store;
        var legacy = legacyStore.Load();
        ReadOutcome outcome = legacyStore.LastLoadOutcome;
        string? legacyProject = FindProjectDirectory(legacy);
        string? activeProject = Directory.Exists(_settings.LastProjectDirectory)
            ? Path.GetFullPath(_settings.LastProjectDirectory)
            : legacyProject;

        if (activeProject is not null)
        {
            var projectPaths = ProjectPaths(activeProject);
            var projectStore = new WorkspaceStore(projectPaths);
            bool hasProjectWorkspace =
                File.Exists(projectPaths.WorkspaceFile) ||
                File.Exists(projectPaths.WorkspaceFile + ".bak");

            _store = projectStore;
            if (hasProjectWorkspace)
            {
                _workspace = projectStore.Load();
                outcome = projectStore.LastLoadOutcome;
            }
            else if (legacyProject is not null && SamePath(legacyProject, activeProject))
            {
                _workspace = legacy;
                _workspace.ProjectDirectory = activeProject;
                try { projectStore.Save(_workspace); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    ShowRecoveryNotice($"Não foi possível mover o canvas para o projeto: {e.Message}");
                }
            }
            else
            {
                _workspace = projectStore.Load();
                outcome = projectStore.LastLoadOutcome;
            }

            _workspace.ProjectDirectory = activeProject;
            RememberProject(activeProject);
        }
        else
        {
            _workspace = legacy;
        }

        RestoreWorkspace(outcome);
    }

    private void RestoreWorkspace(ReadOutcome outcome)
    {
        // WorkspaceStore guarantees at least one tab and an ActiveTabId that points at a real one.
        _canvas = _workspace.Tabs.First(tab => tab.Id == _workspace.ActiveTabId);
        _offsetX = _canvas.Camera.OffsetX;
        _offsetY = _canvas.Camera.OffsetY;
        // WorkspaceStore already clamped this into Camera's range; clamping again here is how the
        // same invariant ends up half-enforced in two places.
        _zoom = _canvas.Camera.Zoom;

        string? activeProject = FindProjectDirectory(_workspace);
        if (activeProject is not null)
        {
            _workingDirectory = activeProject;
            _workspace.ProjectDirectory = activeProject;
        }

        // Restoring the saved graph is not an edit. AddNode schedules an autosave, so without this
        // guard every launch writes one second later with no user input — and after a recovery from
        // .bak that write rotates the corrupt primary into the backup slot, destroying the only
        // good copy while the user is still reading the warning.
        // Every tab is materialized, not just the visible one: a terminal on another canvas is a
        // running process, and the product's whole point is that it keeps working out of sight.
        _loading = true;
        try
        {
            foreach (var tab in _workspace.Tabs)
            {
                // Materialize appends to the tab's list, so iterate a snapshot and start from empty.
                var saved = tab.Nodes.ToList();
                tab.Nodes.Clear();
                foreach (var model in saved) Materialize(model, tab: tab);
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateProjectLabel();
        UpdateZoomLabel();
        RefreshTabStrip();
        // The context bar is built visible in XAML and only ever hidden by a tool change, so
        // without this the size and text controls sit there before any tool has been picked.
        UpdateCanvasToolContext();
        ApplyLayout();

        if (outcome == ReadOutcome.Backup)
            ShowRecoveryNotice("O workspace principal estava corrompido. Recuperado a partir do backup.");

        // Unreadable means a file is there that we could not open or parse — a lock from antivirus,
        // a sync client or a second instance. Seeding would schedule a write over content that is
        // probably intact, so we seed nothing and refuse to save for the rest of the session.
        if (outcome == ReadOutcome.Unreadable)
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
        if (outcome == ReadOutcome.None)
        {
            var first = NextSpawnPoint();
            CreateTerminal(
                first.X,
                first.Y,
                title: Directory.Exists(_workingDirectory)
                    ? $"{ProjectName(_workingDirectory)} · Terminal"
                    : null);
        }
    }

    private static string? FindProjectDirectory(Workspace workspace)
    {
        if (Directory.Exists(workspace.ProjectDirectory))
            return Path.GetFullPath(workspace.ProjectDirectory);

        return workspace.Tabs.SelectMany(tab => tab.Nodes).Select(node => node switch
            {
                TerminalNode terminal => terminal.WorkingDirectory,
                NoteNode note => note.WorkingDirectory,
                _ => ""
            })
            .LastOrDefault(Directory.Exists);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static TetherPaths ProjectPaths(string projectDirectory) =>
        new(Path.Combine(projectDirectory, ".tether"));

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
