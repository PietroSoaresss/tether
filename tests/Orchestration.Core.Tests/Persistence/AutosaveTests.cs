using Microsoft.Extensions.Time.Testing;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class AutosaveTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    [Fact]
    public void Touch_SavesOnceTheDelayElapses()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        using var autosave = new Autosave(() => saves++, Delay, time);

        autosave.Touch();
        Assert.Equal(0, saves);

        time.Advance(Delay);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Touch_ManyTimesInABurst_SavesOnce()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        using var autosave = new Autosave(() => saves++, Delay, time);

        for (int i = 0; i < 100; i++)
        {
            autosave.Touch();
            time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(0, saves);
        time.Advance(Delay);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void FlushNow_SavesImmediatelyAndCancelsThePendingWrite()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        using var autosave = new Autosave(() => saves++, Delay, time);

        autosave.Touch();
        autosave.FlushNow();
        Assert.Equal(1, saves);

        time.Advance(Delay);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void FlushNow_WithNothingPending_DoesNotSave()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        using var autosave = new Autosave(() => saves++, Delay, time);

        autosave.FlushNow();
        Assert.Equal(0, saves);
    }

    [Fact]
    public void Dispose_DropsThePendingWrite()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        var autosave = new Autosave(() => saves++, Delay, time);

        autosave.Touch();
        autosave.Dispose();
        time.Advance(Delay);

        // Closing the app calls FlushNow explicitly; Dispose alone must not fire a write.
        Assert.Equal(0, saves);
    }
}
