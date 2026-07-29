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
    private enum State { Ground, Escape, EscapeIntermediate, Csi, StringSeq, StringSeqEscape }

    // A CSI longer than this is malformed; stop buffering rather than grow forever.
    private const int MaxCsiLength = 64;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _csi = new();
    private State _state = State.Ground;
    private char[] _chars = new char[1024];

    /// <summary>True while the child is painting on an alternate screen buffer (ESC[?1049h, ?1047, ?47).</summary>
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
                // An intermediate byte means a longer form such as the charset designation
                // ESC ( 0, which curses-style TUIs emit for box drawing. Treating it as a
                // two-character escape would spill its final byte into the output.
                else if (c >= '\x20' && c <= '\x2f') _state = State.EscapeIntermediate;
                // Everything else really is two characters (ESC 7, ESC =, ESC c ...).
                else _state = State.Ground;
                break;

            case State.EscapeIntermediate:
                // Intermediates may repeat; anything outside their range is the final byte.
                if (c < '\x20' || c > '\x2f') _state = State.Ground;
                break;

            case State.Csi:
                // Parameter and intermediate bytes are 0x20-0x3F, the final byte is 0x40-0x7E.
                if (c >= '\x40' && c <= '\x7e') { FinishCsi(c); _state = State.Ground; }
                // Stop buffering an overlong, malformed CSI but keep consuming it. Returning
                // to Ground here would emit the rest of the sequence as literal text, which is
                // the exact opposite of this class's job.
                else if (_csi.Length < MaxCsiLength) _csi.Append(c);
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
