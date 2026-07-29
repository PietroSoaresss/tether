using Orchestration.App.Views;
using Orchestration.Core.Models;

namespace Orchestration.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, NoteNodeView> _noteViews = new();
    private readonly Dictionary<string, FileSystemWatcher> _noteWatchers =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _noteReload;

    private static string? NoteFolder(NoteNode model) =>
        string.IsNullOrWhiteSpace(model.WorkingDirectory)
            ? null
            : Path.Combine(model.WorkingDirectory, "notes");

    private void BindNote(NoteNodeView view, NoteNode model)
    {
        string? folder = NoteFolder(model);

        try
        {
            if (string.IsNullOrWhiteSpace(model.FileName))
            {
                model.FileName = _noteFiles.CreateUniqueName(model.Title, folder);
                _noteFiles.Write(model.FileName, "# Nota\n\nTexto em markdown.", folder);
            }
            else if (folder is not null &&
                     !_noteFiles.Exists(model.FileName, folder) &&
                     _noteFiles.Exists(model.FileName))
            {
                // Preserve the legacy AppData copy as a backup; the project receives the live file.
                _noteFiles.Write(model.FileName, _noteFiles.Read(model.FileName), folder);
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowRecoveryNotice($"Não foi possível preparar a nota: {e.Message}");
        }

        WatchNoteFolder(folder ?? _paths.NotesFolder);
        view.Title = model.Title;
        view.ViewMode = model.ViewMode;
        LoadNote(view, model);
        view.MarkdownChanged += _ =>
        {
            try { _noteFiles.Write(model.FileName, view.Markdown, NoteFolder(model)); }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ShowRecoveryNotice($"Não foi possível salvar a nota: {e.Message}");
            }
        };
        view.ViewModeChanged += (_, mode) =>
        {
            model.ViewMode = mode;
            _autosave.Touch();
        };
        view.RecreateRequested += _ =>
        {
            try
            {
                _noteFiles.Write(model.FileName, view.Markdown, NoteFolder(model));
                view.FileMissing = false;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ShowRecoveryNotice($"Não foi possível recriar a nota: {e.Message}");
            }
        };
        _noteViews[model.Id] = view;
    }

    private void LoadNote(NoteNodeView view, NoteNode model)
    {
        try
        {
            string? folder = NoteFolder(model);
            if (!_noteFiles.Exists(model.FileName, folder))
            {
                view.FileMissing = true;
                return;
            }
            string disk = _noteFiles.Read(model.FileName, folder);
            if (view.Markdown != disk) view.Markdown = disk;
            view.FileMissing = false;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowRecoveryNotice($"Não foi possível ler a nota: {e.Message}");
        }
    }

    private void AttachUnassignedNotes(string projectDirectory)
    {
        string folder = Path.Combine(projectDirectory, "notes");
        bool changed = false;
        foreach (var note in _workspace.Nodes.OfType<NoteNode>()
                     .Where(note => string.IsNullOrWhiteSpace(note.WorkingDirectory)))
        {
            try
            {
                string content = _noteViews.TryGetValue(note.Id, out var view)
                    ? view.Markdown
                    : !string.IsNullOrWhiteSpace(note.FileName) && _noteFiles.Exists(note.FileName)
                        ? _noteFiles.Read(note.FileName)
                        : "# Nota\n\nTexto em markdown.";
                string fileName = note.FileName;
                if (string.IsNullOrWhiteSpace(fileName) ||
                    (_noteFiles.Exists(fileName, folder) &&
                     _noteFiles.Read(fileName, folder) != content))
                    fileName = _noteFiles.CreateUniqueName(note.Title, folder);

                if (!_noteFiles.Exists(fileName, folder))
                    _noteFiles.Write(fileName, content, folder);
                note.WorkingDirectory = projectDirectory;
                note.FileName = fileName;
                WatchNoteFolder(folder);
                if (view is not null) LoadNote(view, note);
                changed = true;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ShowRecoveryNotice($"Não foi possível mover a nota para o projeto: {e.Message}");
            }
        }
        if (changed) _autosave.Touch();
    }

    private void WatchNoteFolder(string folder)
    {
        if (_noteWatchers.ContainsKey(folder)) return;
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowRecoveryNotice($"Não foi possível observar as notas: {e.Message}");
            return;
        }

        var watcher = new FileSystemWatcher(folder, "*.md")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Changed += OnNoteFileChanged;
        watcher.Created += OnNoteFileChanged;
        watcher.Deleted += OnNoteFileChanged;
        watcher.Renamed += OnNoteFileChanged;
        _noteWatchers[folder] = watcher;
    }

    private void OnNoteFileChanged(object sender, FileSystemEventArgs e)
    {
        _noteReload?.Cancel();
        var pending = _noteReload = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, pending.Token);
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var entry in _nodes)
                        if (entry.Model is NoteNode note && _noteViews.TryGetValue(note.Id, out var view))
                            LoadNote(view, note);
                });
            }
            catch (OperationCanceledException) { }
        });
    }
}
