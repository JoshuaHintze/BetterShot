using System.Diagnostics;

namespace BetterShot.Recording;

/// <summary>
/// Recording timeline that excludes paused time. Video frames and audio samples are both stamped from this clock,
/// which keeps them in sync across pause/resume. Thread-safe. The time source is injectable for diagnostics.
/// </summary>
internal sealed class RecordingClock
{
    private readonly Func<TimeSpan> _now;
    private readonly object _gate = new();
    private TimeSpan _startedAt;
    private TimeSpan _pausedTotal;
    private TimeSpan? _pausedAt;
    private TimeSpan? _stoppedAt;
    private bool _started;

    internal RecordingClock() : this(MonotonicNow) { }

    internal RecordingClock(Func<TimeSpan> now) => _now = now ?? throw new ArgumentNullException(nameof(now));

    /// <summary>QueryPerformanceCounter-based time, the same base Windows uses for capture frame timestamps.</summary>
    internal static TimeSpan MonotonicNow() => TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    internal bool IsStarted { get { lock (_gate) return _started; } }
    internal bool IsPaused { get { lock (_gate) return _pausedAt is not null; } }
    internal bool IsStopped { get { lock (_gate) return _stoppedAt is not null; } }

    /// <summary>Raw time-source reading, used to relate other clocks (e.g. the camera start) to this timeline.</summary>
    internal TimeSpan Now => _now();

    internal TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                if (!_started) return TimeSpan.Zero;
                TimeSpan end = _stoppedAt ?? _pausedAt ?? _now();
                TimeSpan elapsed = end - _startedAt - _pausedTotal;
                return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
            }
        }
    }

    /// <summary>Raw time-source reading at which the timeline started (zero before <see cref="Start"/>).</summary>
    internal TimeSpan StartedAt { get { lock (_gate) return _startedAt; } }

    internal void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _startedAt = _now();
            _started = true;
        }
    }

    internal bool Pause()
    {
        lock (_gate)
        {
            if (!_started || _pausedAt is not null || _stoppedAt is not null) return false;
            _pausedAt = _now();
            return true;
        }
    }

    internal bool Resume()
    {
        lock (_gate)
        {
            if (_pausedAt is not TimeSpan pausedAt || _stoppedAt is not null) return false;
            _pausedTotal += _now() - pausedAt;
            _pausedAt = null;
            return true;
        }
    }

    /// <summary>Freezes the timeline. A paused clock stops at the moment it was paused.</summary>
    internal void Stop()
    {
        lock (_gate)
        {
            if (!_started || _stoppedAt is not null) return;
            _stoppedAt = _pausedAt ?? _now();
        }
    }
}
