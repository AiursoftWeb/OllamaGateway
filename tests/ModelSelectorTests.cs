using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class ModelSelectorTests
{
    private ModelSelector _selector = null!;

    [TestInitialize]
    public void Setup()
    {
        _selector = new ModelSelector();
    }

    [TestMethod]
    public void TestPriorityFallbackStrategy()
    {
        var vm = new VirtualModel
        {
            Name = "test-model",
            SelectionStrategy = SelectionStrategy.PriorityFallback,
            VirtualModelBackends = new List<VirtualModelBackend>
            {
                new() { Id = 1, Priority = 2, Enabled = true, IsHealthy = true, UnderlyingModelName = "m1" },
                new() { Id = 2, Priority = 1, Enabled = true, IsHealthy = true, UnderlyingModelName = "m2" },
                new() { Id = 3, Priority = 3, Enabled = true, IsHealthy = true, UnderlyingModelName = "m3" }
            }
        };

        var selected = _selector.SelectBackend(vm);
        Assert.AreEqual(2, selected?.Id);
    }

    [TestMethod]
    public void TestWeightedRandomStrategy()
    {
        var vm = new VirtualModel
        {
            Name = "test-model",
            SelectionStrategy = SelectionStrategy.WeightedRandom,
            VirtualModelBackends = new List<VirtualModelBackend>
            {
                new() { Id = 1, Weight = 100, Enabled = true, IsHealthy = true, UnderlyingModelName = "m1" },
                new() { Id = 2, Weight = 0, Enabled = true, IsHealthy = true, UnderlyingModelName = "m2" }
            }
        };

        // With weight 100 vs 0, Id 1 should always be selected
        for (int i = 0; i < 10; i++)
        {
            var selected = _selector.SelectBackend(vm);
            Assert.AreEqual(1, selected?.Id);
        }
    }

    [TestMethod]
    public void TestRoundRobinStrategy()
    {
        var vm = new VirtualModel
        {
            Id = 100,
            Name = "test-model",
            SelectionStrategy = SelectionStrategy.RoundRobin,
            VirtualModelBackends = new List<VirtualModelBackend>
            {
                new() { Id = 1, Enabled = true, IsHealthy = true, UnderlyingModelName = "m1" },
                new() { Id = 2, Enabled = true, IsHealthy = true, UnderlyingModelName = "m2" }
            }
        };

        var first = _selector.SelectBackend(vm);
        var second = _selector.SelectBackend(vm);
        var third = _selector.SelectBackend(vm);

        Assert.AreNotEqual(first?.Id, second?.Id);
        Assert.AreEqual(first?.Id, third?.Id);
    }

    [TestMethod]
    public void TestHealthAndReadyStatus()
    {
        var vm = new VirtualModel
        {
            Name = "test-model",
            VirtualModelBackends = new List<VirtualModelBackend>
            {
                new() { Id = 1, Enabled = true, IsHealthy = false, IsReady = true, UnderlyingModelName = "m1" }
            }
        };

        var selected = _selector.SelectBackend(vm);
        Assert.AreEqual(1, selected?.Id);
    }

    [TestMethod]
    public void TestBanningLogic()
    {
        var vm = new VirtualModel
        {
            Name = "test-model",
            VirtualModelBackends = new List<VirtualModelBackend>
            {
                new() { Id = 1, Enabled = true, IsHealthy = true, UnderlyingModelName = "m1" }
            }
        };

        // Failure 1 & 2
        _selector.ReportFailure(1);
        _selector.ReportFailure(1);
        Assert.IsNotNull(_selector.SelectBackend(vm));

        // Failure 3: 5^(3-3) = 1 min
        _selector.ReportFailure(1);
        Assert.IsNull(_selector.SelectBackend(vm));
        var banUntil = _selector.GetBanUntil(1);
        Assert.IsNotNull(banUntil);

        // Failure 4: 5^(4-3) = 5 min
        _selector.ReportFailure(1);
        var banUntil2 = _selector.GetBanUntil(1);
        Assert.IsTrue(banUntil2 > banUntil);

        // Success: Clear
        _selector.ReportSuccess(1);
        Assert.IsNotNull(_selector.SelectBackend(vm));
        Assert.IsNull(_selector.GetBanUntil(1));
    }

    [TestMethod]
    public void TestNoAvailableBackends()
    {
        var vm = new VirtualModel
        {
            Name = "test-model",
            VirtualModelBackends = new List<VirtualModelBackend>
            {
                new() { Id = 1, Enabled = false, IsHealthy = true, UnderlyingModelName = "m1" },
                new() { Id = 2, Enabled = true, IsHealthy = false, IsReady = false, UnderlyingModelName = "m2" }
            }
        };

        var selected = _selector.SelectBackend(vm);
        Assert.IsNull(selected);
    }

    [TestMethod]
    public void TestBanCap()
    {
        // 5^(10-3) = 78125 minutes > 1440 minutes cap
        for (int i = 0; i < 10; i++)
        {
            _selector.ReportFailure(99);
        }
        
        var banUntil = _selector.GetBanUntil(99);
        Assert.IsNotNull(banUntil);
        var diff = banUntil.Value - DateTime.UtcNow;
        Assert.IsTrue(diff.TotalMinutes > 1430 && diff.TotalMinutes <= 1440);
    }
}
