namespace Orchestration.Core.Persistence;

/// <summary>
/// Coalesces a burst of model changes into a single write. Typing in a note fires a change per
/// keystroke; without the debounce every one of them would rewrite workspace.json.
/// </summary>
public sealed class Autosave : IDisposable
{
    private readonly Action _save;
    private readonly TimeSpan _delay;
    private readonly object _gate = new();

    private ITimer? _timer;
    private bool _pending;
    private bool _disposed;

    public Autosave(Action save, TimeSpan delay, TimeProvider? time = null)
    {
        _save = save;
        _delay = delay;
        _timer = (time ?? TimeProvider.System).CreateTimer(
            _ => Fire(), null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Marks the model dirty and (re)starts the debounce window.</summary>
    public void Touch()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            _timer?.Change(_delay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes right now if anything is pending. Called when the window closes.</summary>
    public void FlushNow()
    {
        lock (_gate)
        {
            if (!_pending || _disposed) return;
            _pending = false;
            _timer?.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
        }
        _save();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (!_pending || _disposed) return;
            _pending = false;
        }
        _save();
    }
}
