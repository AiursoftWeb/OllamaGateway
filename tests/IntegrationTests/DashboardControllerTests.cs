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
    public async Task GetIndex_AuthenticatedWithoutPermission_RedirectsToAccessDenied()
    {
        await RegisterAndLoginAsync();
        var url = "/Dashboard/Index";

        var response = await Http.GetAsync(url);

        // Assert — a regular user without CanViewSystemContext should be denied.
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.IsTrue(location.Contains("Code403"), $"Expected redirect to access denied, got: {location}");
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
    public async Task GetGuide_DescribesCurrentSemanticIrArchitecture()
    {
        await LoginAsAdmin();

        var response = await Http.GetAsync("/Dashboard/Guide");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("POST /v1/messages", html);
        Assert.Contains("POST /v1/responses", html);
        Assert.Contains("data-guide-architecture=\"semantic-ir-v2\"", html);
        Assert.Contains("data-stage=\"request-ir\"", html);
        Assert.Contains("data-stage=\"per-attempt-adapter\"", html);
        Assert.Contains("data-stage=\"event-ir\"", html);
        Assert.Contains("data-route-mode=\"same-dialect\"", html);
        Assert.Contains("data-route-mode=\"cross-dialect\"", html);
        Assert.Contains("data-responses-contract=\"native-state\"", html);
        Assert.Contains("data-guide-status=\"default-model-missing\"", html);
        Assert.Contains("data-retry-semantics=\"attempt-budget\"", html);
        Assert.Contains("data-timeout-scope=\"headers-only\"", html);
        Assert.Contains("data-dashboard-timeouts=\"ollama-ps-3-version-5-openai-models-10\"", html);
        Assert.DoesNotContain("Any OpenAI-compatible or Ollama-native client sends a request.", html);
        Assert.DoesNotContain("feeds the Dashboard charts in real-time", html);
        Assert.DoesNotContain("Every request is logged asynchronously", html);
    }

    [TestMethod]
    public async Task GetIndex_WithProviderAndModels_ShowsCorrectModelCount()
    {
        await LoginAsAdmin();

        var db = GetService<TemplateDbContext>();
        var provider = new OllamaProvider
        {
            Name = "Stats Provider",
            BaseUrl = "http://localhost:11434",
            ProviderType = ProviderType.Ollama
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();

        var vm1 = new VirtualModel { Name = "vm1:latest", Type = ModelType.Chat };
        var vm2 = new VirtualModel { Name = "vm2:latest", Type = ModelType.Chat };
        db.VirtualModels.AddRange(vm1, vm2);
        await db.SaveChangesAsync();

        db.VirtualModelBackends.Add(new VirtualModelBackend
        {
            VirtualModelId = vm1.Id,
            ProviderId = provider.Id,
            UnderlyingModelName = "p1",
            Enabled = true
        });
        db.VirtualModelBackends.Add(new VirtualModelBackend
        {
            VirtualModelId = vm2.Id,
            ProviderId = provider.Id,
            UnderlyingModelName = "p2",
            Enabled = true
        });
        await db.SaveChangesAsync();

        var url = "/Dashboard/Index";
        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Stats Provider"));
        Assert.IsTrue(content.Contains("2</td>"), "Should show model count of 2 for Stats Provider");
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
