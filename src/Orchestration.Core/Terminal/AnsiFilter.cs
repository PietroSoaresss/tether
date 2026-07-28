using System.Text;

namespace Orchestration.Core.Terminal;

/// <summary>
/// Streaming VT sequence stripper. The pseudoconsole hands us 4 KB reads, which cut escape
/// sequences and UTF-8 codepoints in half constantly, so parser state has to survive between
/// calls. A per-chunk regex cannot do this and would silently leak escape bytes downstream.
/// One instance per stream: stateful, not thread-safe.
/// </summary>
public sealed class AnsiFilter
{
    private enum State { Ground, Escape, Csi, StringSeq, StringSeqEscape }

    // A CSI longer than this is malformed; bail out rather than buffer forever.
    private const int MaxCsiLength = 64;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _csi = new();
    private State _state = State.Ground;
    private char[] _chars = new char[1024];

    /// <summary>True while the child is painting on the alternate screen buffer (ESC[?1049h).</summary>
    public bool InAltScreen { get; private set; }

    public string Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return string.Empty;

        // Decode first: every escape byte is ASCII, and UTF-8 continuation bytes are >= 0x80,
        // so decoding can never turn payload into a false escape.
        int needed = _decoder.GetCharCount(chunk, flush: false);
        if (_chars.Length < needed) _chars = new char[needed];
        int count = _decoder.GetChars(chunk, _chars, flush: false);

        var output = new StringBuilder(count);
        for (int i = 0; i < count; i++) Step(_chars[i], output);
        return output.ToString();
    }

    private void Step(char c, StringBuilder output)
    {
        switch (_state)
        {
            case State.Ground:
                if (c == '\x1b') _state = State.Escape;
                else if (c == '\x9b') { _csi.Clear(); _state = State.Csi; }
                else if (c == '\x7f') { }
                else if (c < ' ' && c != '\r' && c != '\n' && c != '\t') { }
                else output.Append(c);
                break;

            case State.Escape:
                if (c == '[') { _csi.Clear(); _state = State.Csi; }
                // OSC, DCS, PM, APC and SOS all run until a string terminator.
                else if (c is ']' or 'P' or '^' or '_' or 'X') _state = State.StringSeq;
                // Everything else is a two-character escape (ESC 7, ESC =, ESC c ...).
                else _state = State.Ground;
                break;

            case State.Csi:
                // Parameter and intermediate bytes are 0x20-0x3F, the final byte is 0x40-0x7E.
                if (c >= '\x40' && c <= '\x7e') { FinishCsi(c); _state = State.Ground; }
                else if (_csi.Length >= MaxCsiLength) _state = State.Ground;
                else _csi.Append(c);
                break;

            case State.StringSeq:
                if (c == '\a') _state = State.Ground;
                else if (c == '\x1b') _state = State.StringSeqEscape;
                break;

            case State.StringSeqEscape:
                _state = c == '\\' ? State.Ground : State.StringSeq;
                break;
        }
    }

    private void FinishCsi(char final)
    {
        // Only one CSI changes anything downstream: the alternate screen toggle. A fullscreen
        // TUI repaints a whole grid every frame, which is noise rather than transcript.
        if (final is not ('h' or 'l')) return;

        string parameters = _csi.ToString();
        if (parameters is "?1049" or "?1047" or "?47") InAltScreen = final == 'h';
    }
}
