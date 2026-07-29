using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Orchestration.Core.Persistence;

public sealed partial class NoteFiles
{
    private readonly TetherPaths _paths;

    public NoteFiles(TetherPaths paths) => _paths = paths;

    public string CreateUniqueName(string title, string? notesFolder = null)
    {
        string folder = Folder(notesFolder);
        Directory.CreateDirectory(folder);
        string stem = Slug(title);
        string name = stem + ".md";
        for (int suffix = 2; File.Exists(Resolve(name, folder)); suffix++) name = $"{stem}-{suffix}.md";
        return name;
    }

    public string Read(string fileName, string? notesFolder = null) =>
        File.ReadAllText(Resolve(fileName, notesFolder));

    public void Write(string fileName, string contents, string? notesFolder = null)
    {
        string folder = Folder(notesFolder);
        Directory.CreateDirectory(folder);
        string path = Resolve(fileName, folder);
        AtomicFile.Write(path, contents);
        if (notesFolder is null || !File.Exists(path + ".bak")) return;

        // Keep crash recovery without cluttering the user's project tree.
        try
        {
            string backup = BackupPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(path + ".bak", backup, overwrite: true);
            File.Delete(path + ".bak");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The sibling backup remains valid if it could not be archived.
        }
    }

    public bool Exists(string fileName, string? notesFolder = null) =>
        File.Exists(Resolve(fileName, notesFolder));

    public string Resolve(string fileName, string? notesFolder = null)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.GetFileName(fileName) != fileName ||
            !Path.GetExtension(fileName).Equals(".md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Note file must be a .md file name inside the notes folder.", nameof(fileName));

        string root = Path.TrimEndingDirectorySeparator(Folder(notesFolder)) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Note path escapes the notes folder.", nameof(fileName));
        return path;
    }

    private string Folder(string? notesFolder) =>
        Path.GetFullPath(notesFolder ?? _paths.NotesFolder);

    private string BackupPath(string path)
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
        return Path.Combine(_paths.Root, "note-backups", $"{id}-{Path.GetFileName(path)}.bak");
    }

    private static string Slug(string title)
    {
        string normalized = title.Normalize(NormalizationForm.FormD);
        var ascii = new string(normalized
            .Where(c => c <= 127 && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '-'))
            .ToArray())
            .ToLowerInvariant();
        string slug = Separators().Replace(ascii.Trim(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "nota" : slug;
    }

    [GeneratedRegex(@"[\s-]+")]
    private static partial Regex Separators();
}
