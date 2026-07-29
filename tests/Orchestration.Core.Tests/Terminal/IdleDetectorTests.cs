using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orchestration.Core.Terminal;
using Xunit;

namespace Orchestration.Core.Tests;

public class IdleDetectorTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Completion_ResolvesOnceOutputGoesQuiet()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("resposta\n"));
        Assert.False(detector.Completion.IsCompleted);

        time.Advance(Idle);

        var result = await detector.Completion;
        Assert.Equal(TurnOutcome.Idle, result.Outcome);
        Assert.Equal("resposta", result.Text);
    }

    [Fact]
    public void Push_ResetsTheIdleWindow()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("a\n"));
        time.Advance(TimeSpan.FromMilliseconds(1400));
        detector.Push(Utf8("b\n"));
        time.Advance(TimeSpan.FromMilliseconds(1400));

        Assert.False(detector.Completion.IsCompleted);
    }

    [Fact]
    public async Task Completion_HitsTheHardTimeout_WhenOutputNeverStops()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        // A source that keeps chattering would reset the idle window forever.
        for (int i = 0; i < 200; i++)
        {
            detector.Push(Utf8($"tick {i}\n"));
            time.Advance(TimeSpan.FromMilliseconds(1000));
        }

        var result = await detector.Completion;
        Assert.Equal(TurnOutcome.Timeout, result.Outcome);
        Assert.Contains("tick", result.Text);
    }

    [Fact]
    public async Task Complete_HandsBackWhatArrivedSoFar()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("meia resp"));
        detector.Complete(TurnOutcome.TargetExited);

        var result = await detector.Completion;
        Assert.Equal(TurnOutcome.TargetExited, result.Outcome);
        Assert.Equal("meia resp", result.Text);
    }

    [Fact]
    public async Task Text_IsFilteredAndCollapsed()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("\x1b[32mecho P\r\x1b[32mecho PROVA\r\n"));
        detector.Push(Utf8("\x1b]0;titulo\aresposta\r\n"));
        time.Advance(Idle);

        var result = await detector.Completion;
        Assert.Equal("echo PROVA\nresposta", result.Text);
    }

    [Fact]
    public async Task Push_AfterCompletion_IsIgnored()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("primeiro\n"));
        time.Advance(Idle);
        var result = await detector.Completion;

        // result is an immutable snapshot, so asserting on it alone would hold even without
        // the guard. InAltScreen is the one piece of detector state still readable after the
        // turn ends, so it is what actually proves the late chunk was dropped.
        detector.Push(Utf8("\x1b[?1049htarde demais\n"));
        Assert.DoesNotContain("tarde", result.Text);
        Assert.False(detector.InAltScreen);
    }

    [Fact]
    public async Task Completion_ReportsTimeout_WhenTheTargetNeverSaysAnything()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        // Nothing was ever pushed. Resolving as Idle here would be indistinguishable from
        // "the agent answered nothing"; the hard timeout is the honest report.
        time.Advance(Timeout);

        var result = await detector.Completion;
        Assert.Equal(TurnOutcome.Timeout, result.Outcome);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void InAltScreen_ReflectsTheTargetsBuffer()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("\x1b[?1049h"));
        Assert.True(detector.InAltScreen);
    }
}
