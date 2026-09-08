namespace DryCut.Application.Queue;

public enum QueueEtaState
{
    Idle,
    WarmingUp,
    Estimated
}

public sealed record QueueEtaEstimate(
    QueueEtaState State,
    TimeSpan? Remaining,
    int PendingCount,
    bool HasActiveItem,
    int SuccessfulSampleCount,
    string DisplayText);

/// <summary>
/// Estimates the time remaining for a sequential work queue from successful item durations.
/// The estimator has no clock dependency: callers provide elapsed values when the active item
/// progresses or completes.
/// </summary>
public sealed class QueueEtaEstimator
{
    public const int DefaultMinimumSamples = 3;
    public const int DefaultSampleWindowSize = 9;

    private readonly int _minimumSamples;
    private readonly int _sampleWindowSize;
    private readonly Queue<TimeSpan> _durationSamples = new();
    private bool _hasActiveItem;
    private TimeSpan _activeElapsed;
    private int _pendingCount;
    private int _successfulSampleCount;

    public QueueEtaEstimator(
        int minimumSamples = DefaultMinimumSamples,
        int sampleWindowSize = DefaultSampleWindowSize)
    {
        if (minimumSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumSamples), "The minimum sample count must be positive.");
        if (sampleWindowSize < minimumSamples)
            throw new ArgumentOutOfRangeException(nameof(sampleWindowSize), "The sample window must contain at least the minimum sample count.");

        _minimumSamples = minimumSamples;
        _sampleWindowSize = sampleWindowSize;
        Current = CreateEstimate();
    }

    public QueueEtaEstimate Current { get; private set; }

    public int PendingCount => _pendingCount;

    public bool HasActiveItem => _hasActiveItem;

    public int SuccessfulSampleCount => _successfulSampleCount;

    /// <summary>
    /// Adds items that are waiting to start. Items added after the active item are included in
    /// the estimate immediately when enough successful samples exist.
    /// </summary>
    public void Enqueue(int count = 1)
    {
        ValidatePositiveCount(count, nameof(count));
        _pendingCount = checked(_pendingCount + count);
        RefreshEstimate();
    }

    /// <summary>
    /// Removes items that have not started. Removing more items than are pending is rejected so
    /// a caller cannot silently make the estimate disagree with its queue.
    /// </summary>
    public void RemovePending(int count = 1)
    {
        ValidatePositiveCount(count, nameof(count));
        if (count > _pendingCount)
            throw new ArgumentOutOfRangeException(nameof(count), "The number removed cannot exceed the pending item count.");

        _pendingCount -= count;
        RefreshEstimate();
    }

    /// <summary>
    /// Moves one pending item to the active state. Starting an item does not create a duration
    /// sample; only successful completion does.
    /// </summary>
    public void StartNext()
    {
        if (_hasActiveItem)
            throw new InvalidOperationException("An item is already active.");
        if (_pendingCount == 0)
            throw new InvalidOperationException("There are no pending items to start.");

        _pendingCount--;
        _hasActiveItem = true;
        _activeElapsed = TimeSpan.Zero;
        RefreshEstimate();
    }

    /// <summary>
    /// Updates the elapsed time for the active item. Values must be non-negative and monotonic
    /// for that item. This method is intentionally clock-independent for deterministic callers
    /// and tests.
    /// </summary>
    public void UpdateActiveElapsed(TimeSpan elapsed)
    {
        EnsureActiveItem();
        ValidateDuration(elapsed, nameof(elapsed));
        if (elapsed < _activeElapsed)
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Active elapsed time cannot move backwards.");

        _activeElapsed = elapsed;
        RefreshEstimate();
    }

    /// <summary>
    /// Records a successfully completed active item. A positive elapsed duration is retained as
    /// a sample; a zero duration is treated as no sample because it provides no useful estimate.
    /// </summary>
    public void CompleteActive(TimeSpan elapsed)
    {
        EnsureActiveItem();
        ValidateDuration(elapsed, nameof(elapsed));
        if (elapsed < _activeElapsed)
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Active elapsed time cannot move backwards.");

        if (elapsed > TimeSpan.Zero)
        {
            _durationSamples.Enqueue(elapsed);
            _successfulSampleCount = checked(_successfulSampleCount + 1);
            while (_durationSamples.Count > _sampleWindowSize)
                _durationSamples.Dequeue();
        }

        ClearActiveItem();
        RefreshEstimate();
    }

    /// <summary>
    /// Removes the active item without learning from its elapsed duration. Use this for both
    /// cancellation and failure; pending items remain queued.
    /// </summary>
    public void CancelActive() => EndActiveWithoutSample();

    /// <summary>
    /// Removes the active item without learning from its elapsed duration. Use this for failure;
    /// pending items remain queued.
    /// </summary>
    public void FailActive() => EndActiveWithoutSample();

    private void EndActiveWithoutSample()
    {
        EnsureActiveItem();
        ClearActiveItem();
        RefreshEstimate();
    }

    private void ClearActiveItem()
    {
        _hasActiveItem = false;
        _activeElapsed = TimeSpan.Zero;
    }

    private void EnsureActiveItem()
    {
        if (!_hasActiveItem)
            throw new InvalidOperationException("There is no active item.");
    }

    private void RefreshEstimate() => Current = CreateEstimate();

    private QueueEtaEstimate CreateEstimate()
    {
        var itemCount = _pendingCount + (_hasActiveItem ? 1 : 0);
        if (itemCount == 0)
        {
            return new QueueEtaEstimate(
                QueueEtaState.Idle,
                TimeSpan.Zero,
                _pendingCount,
                _hasActiveItem,
                SuccessfulSampleCount,
                QueueEtaFormatter.FormatIdle());
        }

        if (SuccessfulSampleCount < _minimumSamples)
        {
            return new QueueEtaEstimate(
                QueueEtaState.WarmingUp,
                null,
                _pendingCount,
                _hasActiveItem,
                SuccessfulSampleCount,
                QueueEtaFormatter.FormatWarmingUp());
        }

        var typicalDuration = GetTypicalDuration();
        var remaining = _hasActiveItem
            ? TimeSpan.FromTicks(Math.Max(0, typicalDuration.Ticks - _activeElapsed.Ticks))
            : TimeSpan.Zero;
        remaining = SaturatingAdd(remaining, SaturatingMultiply(typicalDuration, _pendingCount));

        return new QueueEtaEstimate(
            QueueEtaState.Estimated,
            remaining,
            _pendingCount,
            _hasActiveItem,
            SuccessfulSampleCount,
            QueueEtaFormatter.Format(remaining));
    }

    private TimeSpan GetTypicalDuration()
    {
        var ordered = _durationSamples.OrderBy(duration => duration.Ticks).ToArray();
        var middle = ordered.Length / 2;
        var ticks = ordered.Length % 2 == 1
            ? ordered[middle].Ticks
            : ordered[middle - 1].Ticks + (ordered[middle].Ticks - ordered[middle - 1].Ticks) / 2;
        return TimeSpan.FromTicks(ticks);
    }

    private static TimeSpan SaturatingAdd(TimeSpan left, TimeSpan right)
    {
        var maxTicks = TimeSpan.MaxValue.Ticks;
        return left.Ticks > maxTicks - right.Ticks
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(left.Ticks + right.Ticks);
    }

    private static TimeSpan SaturatingMultiply(TimeSpan duration, int count)
    {
        if (count == 0 || duration == TimeSpan.Zero)
            return TimeSpan.Zero;
        if (duration.Ticks > TimeSpan.MaxValue.Ticks / count)
            return TimeSpan.MaxValue;

        return TimeSpan.FromTicks(duration.Ticks * count);
    }

    private static void ValidatePositiveCount(int count, string parameterName)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(parameterName, "The count must be positive.");
    }

    private static void ValidateDuration(TimeSpan duration, string parameterName)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(parameterName, "A duration cannot be negative.");
    }
}

public static class QueueEtaFormatter
{
    public static string Format(QueueEtaEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return estimate.State switch
        {
            QueueEtaState.Idle => FormatIdle(),
            QueueEtaState.WarmingUp => FormatWarmingUp(),
            QueueEtaState.Estimated => Format(estimate.Remaining ?? TimeSpan.Zero),
            _ => throw new ArgumentOutOfRangeException(nameof(estimate), "Unknown ETA state.")
        };
    }

    public static string Format(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(remaining), "A remaining duration cannot be negative.");
        if (remaining < TimeSpan.FromMinutes(1))
            return "Finishing soon";

        if (remaining < TimeSpan.FromMinutes(10))
            return $"About {CeilingMinutes(remaining)} min remaining";

        if (remaining < TimeSpan.FromHours(1))
        {
            var roundedMinutes = RoundUpTo(remaining.TotalMinutes, 5);
            return $"About {roundedMinutes} min remaining";
        }

        if (remaining < TimeSpan.FromHours(2))
        {
            var roundedMinutes = RoundUpTo(remaining.TotalMinutes, 15);
            var hours = roundedMinutes / 60;
            var minutes = roundedMinutes % 60;
            return minutes == 0
                ? $"About {hours} hr remaining"
                : $"About {hours} hr {minutes} min remaining";
        }

        var roundedHours = (int)Math.Ceiling(remaining.TotalHours);
        return $"About {roundedHours} hr remaining";
    }

    public static string FormatIdle() => "Nothing queued";

    public static string FormatWarmingUp() => "Estimating remaining time…";

    private static int CeilingMinutes(TimeSpan duration) => Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes));

    private static int RoundUpTo(double value, int increment) => checked((int)(Math.Ceiling(value / increment) * increment));
}
