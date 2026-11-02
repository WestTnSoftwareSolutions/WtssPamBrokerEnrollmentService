using Wtss.PamBrokerEnrollmentService;

namespace Wtss.PamBrokerEnrollmentService.Tests;

public sealed class BatchWindowTests
{
    [Fact]
    public void BatchBecomesDueAfterQuietPeriod()
    {
        var batch = new BatchWindow();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.True(batch.Observe([1, 2], start));
        Assert.False(batch.IsDue(start.AddSeconds(59), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)));
        Assert.True(batch.IsDue(start.AddMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void NewAccountRestartsQuietPeriodButNotMaximumAge()
    {
        var batch = new BatchWindow();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        batch.Observe([1], start);
        batch.Observe([1, 2], start.AddMinutes(9));

        Assert.False(batch.IsDue(start.AddMinutes(9).AddSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)));
        Assert.True(batch.IsDue(start.AddMinutes(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void EmptyObservationClearsBatch()
    {
        var batch = new BatchWindow();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        batch.Observe([1], start);
        batch.Observe([], start.AddSeconds(1));

        Assert.False(batch.IsDue(start.AddHours(1), TimeSpan.Zero, TimeSpan.Zero));
    }
}

