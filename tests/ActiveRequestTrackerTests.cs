using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class ActiveRequestTrackerTests
{
    [TestMethod]
    public void TrackedRequest_MovesToActualBackendWithoutEndingVirtualRequest()
    {
        var tracker = new ActiveRequestTracker();
        var request = tracker.BeginRequest("virtual", "hello", "key");

        Assert.AreEqual(1, tracker.GetAll()["virtual"].ActiveCount);
        Assert.AreEqual(0, tracker.GetBusyPhysicalModels().Count);

        request.SetBackend(1, "primary");
        Assert.IsTrue(tracker.GetBusyPhysicalModels().Contains((1, "primary")));

        request.ClearBackend();
        Assert.AreEqual(1, tracker.GetAll()["virtual"].ActiveCount);
        Assert.AreEqual(0, tracker.GetBusyPhysicalModels().Count);

        request.SetBackend(2, "fallback");
        Assert.IsFalse(tracker.GetBusyPhysicalModels().Contains((1, "primary")));
        Assert.IsTrue(tracker.GetBusyPhysicalModels().Contains((2, "fallback")));

        request.Complete(true, answer: "done");

        Assert.AreEqual(0, tracker.GetAll()["virtual"].ActiveCount);
        Assert.AreEqual(0, tracker.GetBusyPhysicalModels().Count);
        var recent = tracker.GetRecentRequests();
        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual("fallback", recent[0].BackendModelName);
        Assert.AreEqual("hello", recent[0].Question);
        Assert.AreEqual("key", recent[0].ApiKeyName);
        Assert.AreEqual("done", recent[0].Answer);
    }

    [TestMethod]
    public void TrackedRequests_OnSameVirtualModelKeepIndependentPhysicalCounts()
    {
        var tracker = new ActiveRequestTracker();
        var first = tracker.BeginRequest("virtual", "first");
        var second = tracker.BeginRequest("virtual", "second");
        first.SetBackend(1, "model-a");
        second.SetBackend(2, "model-b");

        first.Complete(true);

        Assert.AreEqual(1, tracker.GetAll()["virtual"].ActiveCount);
        Assert.IsFalse(tracker.GetBusyPhysicalModels().Contains((1, "model-a")));
        Assert.IsTrue(tracker.GetBusyPhysicalModels().Contains((2, "model-b")));

        second.Complete(true);
        Assert.AreEqual(0, tracker.GetAll()["virtual"].ActiveCount);
        Assert.AreEqual(0, tracker.GetBusyPhysicalModels().Count);
    }

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

        tracker.EndRequest(modelName, providerId, backendModelName, true);

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

        tracker.EndRequest(modelName, providerId, backendModelName, true);
        Assert.AreEqual(1, all[modelName].ActiveCount);

        busyPhysical = tracker.GetBusyPhysicalModels();
        Assert.AreEqual(1, busyPhysical.Count);

        tracker.EndRequest(modelName, providerId, backendModelName, true);
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

    [TestMethod]
    public void TestApiKeyNameIsStored()
    {
        var tracker = new ActiveRequestTracker();
        var modelName = "test-model";
        var apiKeyName = "my-key";

        tracker.StartRequest(modelName, "hello", 1, "llama2", apiKeyName);

        var all = tracker.GetAll();
        Assert.AreEqual(apiKeyName, all[modelName].ApiKeyName);
    }

    [TestMethod]
    public void TestApiKeyNameDefaultsToEmpty()
    {
        var tracker = new ActiveRequestTracker();

        tracker.StartRequest("test-model", "hello", 1, "llama2");

        var all = tracker.GetAll();
        Assert.AreEqual("", all["test-model"].ApiKeyName);
    }

    [TestMethod]
    public void TestEndRequestStoresErrorMessage()
    {
        var tracker = new ActiveRequestTracker();
        var modelName = "test-model";
        var errorMessage = "System.TimeoutException: The request timed out.";

        tracker.StartRequest(modelName, "hello", 1, "llama2");
        tracker.EndRequest(modelName, 1, "llama2", false, errorMessage);

        var recent = tracker.GetRecentRequests();
        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual("Failed", recent[0].Status);
        Assert.AreEqual(errorMessage, recent[0].ErrorMessage);
    }

    [TestMethod]
    public void TestEndRequestEmptyErrorMessageOnSuccess()
    {
        var tracker = new ActiveRequestTracker();

        tracker.StartRequest("test-model", "hello", 1, "llama2");
        tracker.EndRequest("test-model", 1, "llama2", true, "should be ignored");

        var recent = tracker.GetRecentRequests();
        Assert.AreEqual("Completed", recent[0].Status);
        Assert.AreEqual("should be ignored", recent[0].ErrorMessage);
    }

    [TestMethod]
    public void TestGetErrorSummaryExtractsFirstLine()
    {
        var answer = "System.OperationCanceledException: The operation was canceled.\n   at Foo.Bar()\n   at Baz.Qux()";
        var summary = ActiveRequestTracker.GetErrorSummary(answer);
        Assert.AreEqual("System.OperationCanceledException: The operation was canceled.", summary);
    }

    [TestMethod]
    public void TestGetErrorSummaryHandlesNull()
    {
        var summary = ActiveRequestTracker.GetErrorSummary(null);
        Assert.AreEqual("", summary);
    }

    [TestMethod]
    public void TestGetErrorSummaryHandlesEmpty()
    {
        var summary = ActiveRequestTracker.GetErrorSummary("");
        Assert.AreEqual("", summary);
    }

    [TestMethod]
    public void TestEndRequestStoresAnswer()
    {
        var tracker = new ActiveRequestTracker();
        var modelName = "test-model";
        var answer = "Hello, this is a successful response from the model.";

        tracker.StartRequest(modelName, "hello", 1, "llama2");
        tracker.EndRequest(modelName, 1, "llama2", true, "", answer);

        var recent = tracker.GetRecentRequests();
        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual("Completed", recent[0].Status);
        Assert.AreEqual(answer, recent[0].Answer);
        Assert.AreEqual("", recent[0].ErrorMessage);
    }
}
