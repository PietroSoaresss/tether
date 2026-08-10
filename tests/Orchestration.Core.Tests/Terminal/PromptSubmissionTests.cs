using Microsoft.Extensions.Time.Testing;
using Orchestration.Core.Terminal;
using Xunit;

namespace Orchestration.Core.Tests;

public class PromptSubmissionTests
{
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// The bug this guards against: prompt and Enter in one write. The target TUI reads that
    /// single burst as a paste, drops the carriage return into its input box as a newline, and
    /// the delegated prompt is never sent. Two writes with a gap between them is the fix, so the
    /// assertion is about the boundary, not just the final contents.
    /// </summary>
    [Fact]
    public async Task Send_HoldsTheEnterBackUntilTheGapHasPassed()
    {
        var time = new FakeTimeProvider();
        var writes = new List<string>();

        var sending = PromptSubmission.Send(writes.Add, "resolva isso", Gap, time);

        Assert.Equal(new[] { "resolva isso" }, writes);
        Assert.False(sending.IsCompleted);

        time.Advance(Gap);
        await sending;

        Assert.Equal(new[] { "resolva isso", "\r" }, writes);
    }
}
