using System.Text;
using Orchestration.Core.Terminal;
using Xunit;

namespace Orchestration.Core.Tests;

public class AnsiFilterTests
{
    // A little of everything the real thing emits: erase/home, an OSC title, SGR colour,
    // the alt-screen toggle, a multi-byte codepoint and a wide one.
    private const string Rich =
        "\x1b[2J\x1b[H\x1b]0;pwsh\aPS \x1b[32mC:\\dev\x1b[0m> echo oi\r\n" +
        "oi\r\n\x1b[?1049h\x1b[?1049lcafé ✓\r\n";

    private static byte[] RichBytes() => Encoding.UTF8.GetBytes(Rich);

    [Fact]
    public void Feed_StripsCsiSequences()
    {
        var filter = new AnsiFilter();
        Assert.Equal("red", filter.Feed(Encoding.UTF8.GetBytes("\x1b[31mred\x1b[0m")));
    }

    [Fact]
    public void Feed_KeepsCarriageReturnLineFeedAndTab()
    {
        var filter = new AnsiFilter();
        Assert.Equal("a\r\n\tb", filter.Feed(Encoding.UTF8.GetBytes("a\r\n\tb")));
    }

    [Fact]
    public void Feed_DropsOtherControlCharacters()
    {
        var filter = new AnsiFilter();
        // \u escapes, not \x: C# lets \x swallow 1-4 hex digits, so "\x07a" is U+007A ('z')
        // and "\x00b" is U+000B, which quietly turns this into a different test.
        Assert.Equal("ab", filter.Feed(Encoding.UTF8.GetBytes("\u0007a\u0000b")));
    }

    [Fact]
    public void Feed_StripsOscTerminatedByBel()
    {
        var filter = new AnsiFilter();
        Assert.Equal("X", filter.Feed(Encoding.UTF8.GetBytes("\x1b]0;titulo\aX")));
    }

    [Fact]
    public void Feed_StripsOscTerminatedByStringTerminator()
    {
        var filter = new AnsiFilter();
        Assert.Equal("X", filter.Feed(Encoding.UTF8.GetBytes("\x1b]0;titulo\x1b\\X")));
    }

    [Fact]
    public void Feed_TracksAlternateScreenBuffer()
    {
        var filter = new AnsiFilter();
        Assert.False(filter.InAltScreen);

        filter.Feed(Encoding.UTF8.GetBytes("\x1b[?1049h"));
        Assert.True(filter.InAltScreen);

        filter.Feed(Encoding.UTF8.GetBytes("\x1b[?1049l"));
        Assert.False(filter.InAltScreen);
    }

    /// <summary>
    /// The guarantee that matters: 4 KB reads cut sequences and codepoints in half all day,
    /// so the result must not depend on where the cut lands.
    /// </summary>
    [Fact]
    public void Feed_IsIndependentOfChunkBoundaries()
    {
        byte[] bytes = RichBytes();
        string whole = new AnsiFilter().Feed(bytes);

        for (int split = 1; split < bytes.Length; split++)
        {
            var filter = new AnsiFilter();
            string first = filter.Feed(bytes.AsSpan(0, split));
            string second = filter.Feed(bytes.AsSpan(split));
            Assert.Equal(whole, first + second);
        }
    }

    [Fact]
    public void Feed_IsIndependentOfRandomMultiWaySplits()
    {
        byte[] bytes = RichBytes();
        string whole = new AnsiFilter().Feed(bytes);
        var random = new Random(20260728);

        for (int attempt = 0; attempt < 200; attempt++)
        {
            var filter = new AnsiFilter();
            var rebuilt = new StringBuilder();
            int offset = 0;
            while (offset < bytes.Length)
            {
                int take = random.Next(1, 7);
                take = Math.Min(take, bytes.Length - offset);
                rebuilt.Append(filter.Feed(bytes.AsSpan(offset, take)));
                offset += take;
            }
            Assert.Equal(whole, rebuilt.ToString());
        }
    }

    [Fact]
    public void Feed_HandlesCodepointSplitAcrossChunks()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("café");
        var filter = new AnsiFilter();

        // "é" is two bytes; cut between them.
        string first = filter.Feed(bytes.AsSpan(0, bytes.Length - 1));
        string second = filter.Feed(bytes.AsSpan(bytes.Length - 1));

        Assert.Equal("café", first + second);
    }

    [Fact]
    public void Feed_RecoversFromAnAbsurdlyLongCsi()
    {
        var filter = new AnsiFilter();
        // The sequence must still be consumed to its final byte; bailing out early would
        // spill the leftover parameter bytes into the output as text.
        string garbage = "\x1b[" + new string('0', 200) + "m" + "ok";
        Assert.Equal("ok", filter.Feed(Encoding.UTF8.GetBytes(garbage)));
    }

    [Fact]
    public void Feed_StripsCharsetDesignationEscapes()
    {
        var filter = new AnsiFilter();
        // ESC ( 0 selects DEC special graphics for box drawing, ESC ( B returns to ASCII.
        // These are three bytes, not two, so the final byte must not reach the output.
        Assert.Equal("ok", filter.Feed(Encoding.UTF8.GetBytes("\x1b(0\x1b(Bok")));
    }
}
