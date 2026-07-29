using System.Collections;
using System.Text;

namespace Orchestration.Core.Terminal;

public static class EnvironmentBlock
{
    public static string Build(IReadOnlyDictionary<string, string> overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[(string)entry.Key] = (string?)entry.Value ?? "";
        foreach (var (key, value) in overrides) values[key] = value;

        var block = new StringBuilder();
        foreach (var (key, value) in values) block.Append(key).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }
}
