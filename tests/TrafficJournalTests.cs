using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class TrafficJournalTests
{
    [TestMethod]
    public async Task ReconnectPreservesAttemptsAndRecoversLongRunningRequestsAfterEviction()
    {
        var clock = new BackendMonitoringTests.ManualClock();
        var journal = new TrafficJournal(clock);
        var request = new TrafficEvent(Guid.NewGuid(), 1, 1, 11, 21, "queued");
        journal.Record(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var first = journal.Read(null, timeout.Token).GetAsyncEnumerator();
        Assert.IsTrue(await first.MoveNextAsync());
        var cursor = first.Current.EventId;
        var arrival = first.Current.Data.Events.Single();
        Assert.AreEqual(request.RequestId, arrival.RequestId);
        Assert.AreEqual(request.ModelId, arrival.ModelId);
        Assert.AreEqual(request.ProviderId, arrival.ProviderId);
        Assert.AreEqual(clock.Now.ToUnixTimeMilliseconds(), arrival.At);
        Assert.AreEqual(arrival.At, first.Current.Data.ServerTime);
        clock.Advance(TimeSpan.FromSeconds(3));

        journal.Record(request with { Stage = "attemptFailed" });
        journal.Record(request with { Attempt = 2, BackendId = 12, Stage = "sent" });
        await using var resumed = journal.Read(cursor, timeout.Token).GetAsyncEnumerator();
        Assert.IsTrue(await resumed.MoveNextAsync());
        Assert.IsNull(resumed.Current.Data.Baseline);
        Assert.AreEqual(arrival.Sequence + 1, resumed.Current.Data.Events[0].Sequence);
        Assert.AreEqual(arrival.Sequence + 2, resumed.Current.Data.Events[1].Sequence);
        Assert.AreEqual(3000L, resumed.Current.Data.Events[0].At - arrival.At);
        CollectionAssert.AreEqual(new[] { "attemptFailed", "sent" }, resumed.Current.Data.Events.Select(e => e.Stage).ToArray());

        var shortRequest = request with { RequestId = Guid.NewGuid() };
        for (var i = 0; i < 4200; i++)
            journal.Record(shortRequest with { Stage = i % 2 == 0 ? "received" : "completed" });

        await using var reset = journal.Read(cursor, timeout.Token).GetAsyncEnumerator();
        Assert.IsTrue(await reset.MoveNextAsync());
        var active = reset.Current.Data.Baseline!.Single(e => e.RequestId == request.RequestId);
        Assert.AreEqual(2, active.Attempt);
        Assert.AreEqual(12, active.BackendId);
    }
}
