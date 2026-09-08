using DryCut.Application.Queue;

namespace DryCut.Application.Tests;

public sealed class QueueEtaEstimatorTests
{
    [Fact]
    public void StartsIdleWithNoFalsePrecision()
    {
        var estimator = new QueueEtaEstimator();

        Assert.Equal(QueueEtaState.Idle, estimator.Current.State);
        Assert.Equal(TimeSpan.Zero, estimator.Current.Remaining);
        Assert.Equal("Nothing queued", estimator.Current.DisplayText);
        Assert.Equal(0, estimator.SuccessfulSampleCount);
    }

    [Fact]
    public void FirstQueuedItemIsWarmingUpUntilMinimumEvidenceExists()
    {
        var estimator = new QueueEtaEstimator();
        estimator.Enqueue();

        Assert.Equal(QueueEtaState.WarmingUp, estimator.Current.State);
        Assert.Null(estimator.Current.Remaining);
        Assert.Equal("Estimating remaining time…", estimator.Current.DisplayText);

        estimator.StartNext();
        estimator.UpdateActiveElapsed(TimeSpan.FromSeconds(20));

        Assert.Equal(QueueEtaState.WarmingUp, estimator.Current.State);
        Assert.Null(estimator.Current.Remaining);
        Assert.True(estimator.Current.HasActiveItem);
        Assert.Equal(0, estimator.SuccessfulSampleCount);
    }

    [Fact]
    public void SuccessfulSamplesEnableEstimateForPendingAndActiveWork()
    {
        var estimator = new QueueEtaEstimator();
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(10));
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(12));
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(11));

        estimator.Enqueue(2);
        Assert.Equal(TimeSpan.FromSeconds(22), estimator.Current.Remaining);

        estimator.StartNext();
        estimator.UpdateActiveElapsed(TimeSpan.FromSeconds(4));

        Assert.Equal(QueueEtaState.Estimated, estimator.Current.State);
        Assert.True(estimator.Current.HasActiveItem);
        Assert.Equal(1, estimator.Current.PendingCount);
        Assert.Equal(TimeSpan.FromSeconds(18), estimator.Current.Remaining);
    }

    [Fact]
    public void MedianSampleResistsOneAbsurdDuration()
    {
        var estimator = new QueueEtaEstimator();
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(10));
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(11));
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(5 * 60));
        estimator.Enqueue();

        Assert.Equal(TimeSpan.FromSeconds(11), estimator.Current.Remaining);
    }

    [Fact]
    public void CancellationAndFailureDoNotBecomeSamples()
    {
        var estimator = new QueueEtaEstimator();
        estimator.Enqueue(3);

        estimator.StartNext();
        estimator.UpdateActiveElapsed(TimeSpan.FromMinutes(2));
        estimator.CancelActive();
        Assert.Equal(0, estimator.SuccessfulSampleCount);
        Assert.Equal(2, estimator.PendingCount);
        Assert.Equal(QueueEtaState.WarmingUp, estimator.Current.State);

        estimator.StartNext();
        estimator.FailActive();
        Assert.Equal(0, estimator.SuccessfulSampleCount);
        Assert.Equal(1, estimator.PendingCount);
        Assert.False(estimator.HasActiveItem);
    }

    [Fact]
    public void ZeroDurationSuccessDoesNotPretendToBeUsefulEvidence()
    {
        var estimator = new QueueEtaEstimator();
        estimator.Enqueue();
        estimator.StartNext();
        estimator.CompleteActive(TimeSpan.Zero);

        Assert.Equal(0, estimator.SuccessfulSampleCount);
        Assert.Equal(QueueEtaState.Idle, estimator.Current.State);
    }

    [Fact]
    public void AddingAndRemovingPendingItemsChangesOnlyTheWorkload()
    {
        var estimator = WarmEstimator();
        estimator.Enqueue(3);
        Assert.Equal(3, estimator.Current.PendingCount);
        Assert.Equal(TimeSpan.FromSeconds(30), estimator.Current.Remaining);

        estimator.RemovePending(2);
        Assert.Equal(1, estimator.PendingCount);
        Assert.Equal(TimeSpan.FromSeconds(10), estimator.Current.Remaining);

        estimator.StartNext();
        estimator.Enqueue();
        estimator.RemovePending();
        Assert.Equal(0, estimator.PendingCount);
        Assert.Equal(TimeSpan.FromSeconds(10), estimator.Current.Remaining);

        estimator.CompleteActive(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueEtaState.Idle, estimator.Current.State);
    }

    [Fact]
    public void ActiveElapsedCannotMoveBackwardsAndRemainingNeverGoesNegative()
    {
        var estimator = WarmEstimator();
        estimator.Enqueue();
        estimator.StartNext();
        estimator.UpdateActiveElapsed(TimeSpan.FromSeconds(12));

        Assert.Throws<ArgumentOutOfRangeException>(() => estimator.UpdateActiveElapsed(TimeSpan.FromSeconds(11)));
        Assert.Throws<ArgumentOutOfRangeException>(() => estimator.CompleteActive(TimeSpan.FromSeconds(11)));
        Assert.Equal(TimeSpan.Zero, estimator.Current.Remaining);
    }

    [Fact]
    public void VeryLargeWorkloadsSaturateInsteadOfOverflowing()
    {
        var estimator = new QueueEtaEstimator(minimumSamples: 1, sampleWindowSize: 1);
        CompleteSuccessfully(estimator, TimeSpan.MaxValue);
        estimator.Enqueue(2);

        Assert.Equal(TimeSpan.MaxValue, estimator.Current.Remaining);
    }

    [Fact]
    public void OnlyRecentSamplesAreUsedWhenWindowIsBounded()
    {
        var estimator = new QueueEtaEstimator(minimumSamples: 3, sampleWindowSize: 3);
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(10));
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(10));
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(10));
        CompleteSuccessfully(estimator, TimeSpan.FromMinutes(5));
        estimator.Enqueue();

        Assert.Equal(4, estimator.SuccessfulSampleCount);
        Assert.Equal(TimeSpan.FromSeconds(10), estimator.Current.Remaining);
    }

    [Fact]
    public void InvalidQueueTransitionsAreRejected()
    {
        var estimator = new QueueEtaEstimator();

        Assert.Throws<InvalidOperationException>(() => estimator.StartNext());
        Assert.Throws<InvalidOperationException>(() => estimator.UpdateActiveElapsed(TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => estimator.CompleteActive(TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => estimator.Enqueue(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => estimator.RemovePending(1));

        estimator.Enqueue();
        estimator.StartNext();
        Assert.Throws<InvalidOperationException>(() => estimator.StartNext());
        Assert.Throws<ArgumentOutOfRangeException>(() => estimator.UpdateActiveElapsed(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => estimator.CompleteActive(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData(0, "Finishing soon")]
    [InlineData(59, "Finishing soon")]
    [InlineData(60, "About 1 min remaining")]
    [InlineData(61, "About 2 min remaining")]
    [InlineData(599, "About 10 min remaining")]
    [InlineData(600, "About 10 min remaining")]
    [InlineData(601, "About 15 min remaining")]
    [InlineData(3599, "About 60 min remaining")]
    [InlineData(3600, "About 1 hr remaining")]
    [InlineData(3601, "About 1 hr 15 min remaining")]
    [InlineData(7200, "About 2 hr remaining")]
    public void FormatterRoundsConservativelyWithoutSeconds(int seconds, string expected)
    {
        Assert.Equal(expected, QueueEtaFormatter.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatterUsesSnapshotStateForBinding()
    {
        Assert.Equal("Nothing queued", QueueEtaFormatter.Format(new QueueEtaEstimate(QueueEtaState.Idle, TimeSpan.Zero, 0, false, 0, "ignored")));
        Assert.Equal("Estimating remaining time…", QueueEtaFormatter.Format(new QueueEtaEstimate(QueueEtaState.WarmingUp, null, 1, true, 0, "ignored")));
        Assert.Equal("About 2 min remaining", QueueEtaFormatter.Format(new QueueEtaEstimate(QueueEtaState.Estimated, TimeSpan.FromSeconds(61), 0, true, 3, "ignored")));
    }

    private static QueueEtaEstimator WarmEstimator()
    {
        var estimator = new QueueEtaEstimator();
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(10));
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(10));
        CompleteSuccessfully(estimator, TimeSpan.FromSeconds(10));
        return estimator;
    }

    private static void CompleteSuccessfully(QueueEtaEstimator estimator, TimeSpan elapsed)
    {
        estimator.Enqueue();
        estimator.StartNext();
        estimator.CompleteActive(elapsed);
    }
}
