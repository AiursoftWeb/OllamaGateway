using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class MemoryUsageTrackerTests
{
    [TestMethod]
    public void TestTrackApiKeyModelUsage()
    {
        var tracker = new MemoryUsageTracker(new UsageCounter(), null!);
        var apiKeyId = 1;
        var modelName = "test-model";

        tracker.TrackApiKeyModelUsage(apiKeyId, modelName);
        tracker.TrackApiKeyModelUsage(apiKeyId, modelName);
        tracker.TrackApiKeyModelUsage(apiKeyId, "other-model");

        var breakdown = tracker.GetApiKeyModelBreakdown(apiKeyId);
        Assert.AreEqual(2, breakdown.Count);
        Assert.AreEqual(2, breakdown["test-model"]);
        Assert.AreEqual(1, breakdown["other-model"]);
    }

    [TestMethod]
    public void TestGetApiKeyModelBreakdownEmpty()
    {
        var tracker = new MemoryUsageTracker(new UsageCounter(), null!);

        var breakdown = tracker.GetApiKeyModelBreakdown(999);

        Assert.AreEqual(0, breakdown.Count);
    }

    [TestMethod]
    public void TestGetApiKeyModelBreakdownOrderedDescending()
    {
        var tracker = new MemoryUsageTracker(new UsageCounter(), null!);
        var apiKeyId = 1;

        tracker.TrackApiKeyModelUsage(apiKeyId, "model-a");
        tracker.TrackApiKeyModelUsage(apiKeyId, "model-b");
        tracker.TrackApiKeyModelUsage(apiKeyId, "model-b");
        tracker.TrackApiKeyModelUsage(apiKeyId, "model-b");
        tracker.TrackApiKeyModelUsage(apiKeyId, "model-c");
        tracker.TrackApiKeyModelUsage(apiKeyId, "model-c");

        var breakdown = tracker.GetApiKeyModelBreakdown(apiKeyId);
        var keys = breakdown.Keys.ToList();
        Assert.AreEqual("model-b", keys[0]);
        Assert.AreEqual("model-c", keys[1]);
        Assert.AreEqual("model-a", keys[2]);
    }
}
