using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class ActiveRequestTrackerTests
{
    [TestMethod]
    public void TestStartAndEndRequest()
    {
        var tracker = new ActiveRequestTracker();
        var modelName = "test-model";
        var question = "hello";
        var providerId = 1;
        var backendModelName = "llama2";

        tracker.StartRequest(modelName, question, providerId, backendModelName);

        var all = tracker.GetAll();
        Assert.IsTrue(all.ContainsKey(modelName));
        Assert.AreEqual(1, all[modelName].ActiveCount);
        Assert.AreEqual(question, all[modelName].LastQuestion);
        Assert.AreEqual(backendModelName, all[modelName].BackendModelName);

        var busyPhysical = tracker.GetBusyPhysicalModels();
        Assert.AreEqual(1, busyPhysical.Count);
        Assert.IsTrue(busyPhysical.Contains((providerId, backendModelName)));

        tracker.EndRequest(modelName, providerId, backendModelName);

        Assert.AreEqual(0, all[modelName].ActiveCount);
        Assert.IsNotNull(all[modelName].LastCompletedAt);
        
        busyPhysical = tracker.GetBusyPhysicalModels();
        Assert.AreEqual(0, busyPhysical.Count);
    }

    [TestMethod]
    public void TestMultipleRequests()
    {
        var tracker = new ActiveRequestTracker();
        var modelName = "test-model";
        var providerId = 1;
        var backendModelName = "llama2";

        tracker.StartRequest(modelName, "q1", providerId, backendModelName);
        tracker.StartRequest(modelName, "q2", providerId, backendModelName);

        var all = tracker.GetAll();
        Assert.AreEqual(2, all[modelName].ActiveCount);

        var busyPhysical = tracker.GetBusyPhysicalModels();
        Assert.AreEqual(1, busyPhysical.Count);

        tracker.EndRequest(modelName, providerId, backendModelName);
        Assert.AreEqual(1, all[modelName].ActiveCount);
        
        busyPhysical = tracker.GetBusyPhysicalModels();
        Assert.AreEqual(1, busyPhysical.Count);

        tracker.EndRequest(modelName, providerId, backendModelName);
        Assert.AreEqual(0, all[modelName].ActiveCount);
        
        busyPhysical = tracker.GetBusyPhysicalModels();
        Assert.AreEqual(0, busyPhysical.Count);
    }

    [TestMethod]
    public void TestLongQuestionTruncation()
    {
        var tracker = new ActiveRequestTracker();
        var modelName = "test-model";
        var longQuestion = new string('a', 100);
        var providerId = 1;
        var backendModelName = "llama2";

        tracker.StartRequest(modelName, longQuestion, providerId, backendModelName);

        var all = tracker.GetAll();
        Assert.AreEqual(30, all[modelName].LastQuestion.Length);
        Assert.AreEqual(new string('a', 30), all[modelName].LastQuestion);
    }
}
