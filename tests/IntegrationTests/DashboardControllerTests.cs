using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class DashboardControllerTests : TestBase
{
    [TestMethod]
    public async Task GetIndex_NotAuthenticated_RedirectsToLogin()
    {
        var url = "/Dashboard/Index";
        
        var response = await Http.GetAsync(url);
        
        // Assert
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }

    [TestMethod]
    public async Task GetIndex_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/Dashboard/Index";
        
        var response = await Http.GetAsync(url);
        
        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Admin Center"));
    }

    [TestMethod]
    public async Task GetMonitor_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/Dashboard/Monitor";
        
        var response = await Http.GetAsync(url);
        
        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Traffic Visualization"));
        Assert.IsTrue(content.Contains("mermaid"));
    }

    [TestMethod]
    public async Task GetMonitor_WithBusyModel_HighlightsModel()
    {
        await LoginAsAdmin();
        
        // Add a virtual model and backend to the DB
        var db = GetService<TemplateDbContext>();
        var provider = new OllamaProvider
        {
            Name = "Test Provider",
            BaseUrl = "http://localhost:11434",
            ProviderType = ProviderType.Ollama
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();

        var vm = new VirtualModel
        {
            Name = "test-virtual-model",
            Type = ModelType.Chat
        };
        db.VirtualModels.Add(vm);
        await db.SaveChangesAsync();

        var backend = new VirtualModelBackend
        {
            VirtualModelId = vm.Id,
            ProviderId = provider.Id,
            UnderlyingModelName = "test-physical-model",
            Enabled = true
        };
        db.VirtualModelBackends.Add(backend);
        await db.SaveChangesAsync();
        
        // Mark a model as busy
        var tracker = GetService<ActiveRequestTracker>();
        tracker.StartRequest(vm.Name, "test question", provider.Id, "test-physical-model");

        var url = "/Dashboard/Monitor";
        var response = await Http.GetAsync(url);
        
        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("virtualSvcBusy"), "Mermaid code should contain virtualSvcBusy class when a model is busy.");
        Assert.IsTrue(content.Contains("physicalBusy"), "Mermaid code should contain physicalBusy class when a physical model is busy.");
    }
}
