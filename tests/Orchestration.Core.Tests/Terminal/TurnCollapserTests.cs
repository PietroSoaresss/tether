using Orchestration.Core.Terminal;
using Xunit;

namespace Orchestration.Core.Tests;

public class TurnCollapserTests
{
    [Fact]
    public void Append_CarriageReturnOverwritesTheCurrentLine()
    {
        var collapser = new TurnCollapser();
        collapser.Append("echo P\recho PR\recho PROVA\n");
        Assert.Equal("echo PROVA", collapser.Result);
    }

    [Fact]
    public void Append_OverwriteShorterThanTheLine_KeepsTheTail()
    {
        var collapser = new TurnCollapser();
        collapser.Append("abcdef\rxy");
        Assert.Equal("xydef", collapser.Result);
    }

    [Fact]
    public void Append_DropsConsecutiveDuplicateLines()
    {
        var collapser = new TurnCollapser();
        collapser.Append("a\na\na\nb\n");
        Assert.Equal("a\nb", collapser.Result);
    }

    [Fact]
    public void Append_KeepsDuplicatesThatAreNotAdjacent()
    {
        var collapser = new TurnCollapser();
        collapser.Append("a\nb\na\n");
        Assert.Equal("a\nb\na", collapser.Result);
    }

    [Fact]
    public void Result_IncludesTheUnterminatedTail()
    {
        var collapser = new TurnCollapser();
        collapser.Append("linha\nparcial");
        Assert.Equal("linha\nparcial", collapser.Result);
    }

    [Fact]
    public void Append_AcrossCallsBehavesLikeOneCall()
    {
        var split = new TurnCollapser();
        split.Append("echo P\recho ");
        split.Append("PROVA\nfim\n");

        var whole = new TurnCollapser();
        whole.Append("echo P\recho PROVA\nfim\n");

        Assert.Equal(whole.Result, split.Result);
    }

    [Fact]
    public void Append_TrimsFromTheFrontWhenOverCap()
    {
        var collapser = new TurnCollapser(capChars: 32);
        for (int i = 0; i < 50; i++) collapser.Append($"linha-{i}\n");

        string result = collapser.Result;
        Assert.True(result.Length <= 32, $"esperado <= 32, veio {result.Length}");
        Assert.DoesNotContain("linha-0\n", result);
        Assert.Contains("linha-49", result);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var collapser = new TurnCollapser();
        collapser.Append("a\nb\n");
        collapser.Reset();
        Assert.Equal("", collapser.Result);
    }
}
