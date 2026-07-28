using System.Text;

namespace Orchestration.Core.Terminal;

/// <summary>
/// Turns filtered terminal text into a transcript.
/// Agent CLIs repaint: the F0 spike caught PSReadLine redrawing the same row as "echo P",
/// then "echo PR", then "echo PROVA_CONP". Concatenating those verbatim produces duplicated
/// text, not a transcript, so carriage-return overwrite and adjacent-duplicate collapse are
/// load-bearing, not polish.
/// </summary>
public sealed class TurnCollapser
{
    public const int DefaultCapChars = 256 * 1024;

    private readonly List<string> _lines = new();
    private readonly StringBuilder _current = new();
    private readonly int _cap;
    private int _column;
    private int _length;

    public TurnCollapser(int capChars = DefaultCapChars) => _cap = capChars;

    public void Append(string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    _column = 0;
                    break;

                case '\n':
                    CommitLine();
                    break;

                default:
                    if (_column < _current.Length) _current[_column] = c;
                    else _current.Append(c);
                    _column++;
                    break;
            }
        }
    }

    public string Result
    {
        get
        {
            if (_current.Length == 0) return string.Join('\n', _lines);
            if (_lines.Count == 0) return _current.ToString();
            return string.Join('\n', _lines) + "\n" + _current;
        }
    }

    public void Reset()
    {
        _lines.Clear();
        _current.Clear();
        _column = 0;
        _length = 0;
    }

    private void CommitLine()
    {
        string line = _current.ToString();
        _current.Clear();
        _column = 0;

        // The same row painted twice in a row is a redraw, not new output.
        if (_lines.Count > 0 && _lines[^1] == line) return;

        _lines.Add(line);
        _length += line.Length + 1;

        // A source that never quiesces (think `yes`) must not grow without bound.
        while (_length > _cap && _lines.Count > 1)
        {
            _length -= _lines[0].Length + 1;
            _lines.RemoveAt(0);
        }
    }
}
