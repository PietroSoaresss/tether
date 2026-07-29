using System.Text;
using System.Text.Json;

namespace Orchestration.Core.Persistence;

public enum ReadOutcome
{
    /// <summary>The primary file parsed cleanly.</summary>
    Primary,
    /// <summary>The primary was missing or unusable and the .bak saved us.</summary>
    Backup,
    /// <summary>Nothing was on disk at all; the caller should start fresh.</summary>
    None,
    /// <summary>
    /// A file was there but nothing could be read or parsed from it. Distinct from
    /// <see cref="None"/> because starting fresh here means writing over content that may be
    /// perfectly good and merely locked.
    /// </summary>
    Unreadable
}

/// <summary>
/// Crash-safe file replacement. Writes a sibling .tmp and swaps it in with File.Replace, which
/// is atomic and hands the previous good content to a .bak. Losing power mid-save would
/// otherwise leave a truncated workspace and nothing to fall back to.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(temporary, path);
    }

    /// <summary>
    /// Reads and parses <paramref name="path"/>, falling back to its .bak. A parse failure counts
    /// as unusable just like an IO failure: a syntactically broken file is the common corruption.
    /// </summary>
    public static ReadOutcome TryRead<T>(string path, Func<string, T?> parse, out T? value) where T : class
    {
        ReadOutcome[] outcomes = { ReadOutcome.Primary, ReadOutcome.Backup };
        string[] candidates = { path, path + ".bak" };
        bool anyCandidateExisted = false;

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!File.Exists(candidates[i])) continue;
            anyCandidateExisted = true;
            try
            {
                T? parsed = parse(File.ReadAllText(candidates[i]));
                if (parsed is null) continue;
                value = parsed;
                return outcomes[i];
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
            {
            }
        }

        value = null;
        // A caller that seeds a fresh document treats None as permission to write. Saying None for
        // a file we merely failed to open would hand it that permission over intact content.
        return anyCandidateExisted ? ReadOutcome.Unreadable : ReadOutcome.None;
    }
}
