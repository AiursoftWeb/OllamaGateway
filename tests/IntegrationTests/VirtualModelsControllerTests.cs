using System.Net;
using Microsoft.EntityFrameworkCore;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class VirtualModelsControllerTests : TestBase
{
    [TestMethod]
    [DataRow("")]
    [DataRow("true")]
    [DataRow("false")]
    public async Task OpenAiThinking_CreateAddUpdateAndProtocolSwitch(string setting)
    {
        await LoginAsAdmin();
        var name = $"openai-thinking-{Guid.NewGuid():N}:latest";
        var db = GetService<TemplateDbContext>();
        var provider = new OllamaProvider { Name = name, BaseUrl = "http://localhost:1", ProviderType = ProviderType.OpenAI };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();
        var created = await PostForm("/VirtualModels/Create", new Dictionary<string, string>
        {
            ["Name"] = name, ["ProviderId"] = provider.Id.ToString(), ["UnderlyingModel"] = "first",
            ["Protocol"] = "1", ["OpenAiThinkOverride"] = setting, ["ReasoningEffortOverride"] = "xhigh"
        });
        Assert.AreEqual(HttpStatusCode.Found, created.StatusCode);
        var model = await db.VirtualModels.Include(v => v.VirtualModelBackends).SingleAsync(v => v.Name == name);
        var backend = model.VirtualModelBackends.Single();
        bool? expected = setting == "" ? null : bool.Parse(setting);
        Assert.AreEqual(expected, backend.OpenAiThinkOverride);
        Assert.AreEqual("xhigh", backend.ReasoningEffortOverride);
        var added = await PostForm($"/VirtualModels/AddBackend/{model.Id}", new Dictionary<string, string>
        {
            ["providerId"] = provider.Id.ToString(), ["underlyingModel"] = "second", ["protocol"] = "2",
            ["openAiThinkOverride"] = setting, ["reasoningEffortOverride"] = "medium"
        }, $"/VirtualModels/Edit/{model.Id}");
        Assert.AreEqual(HttpStatusCode.Found, added.StatusCode);
        var second = await db.VirtualModelBackends.AsNoTracking().SingleAsync(b => b.VirtualModelId == model.Id && b.UnderlyingModelName == "second");
        Assert.AreEqual(expected, second.OpenAiThinkOverride);
        foreach (var protocol in new[] { "2", "0" })
        {
            var updated = await PostForm($"/VirtualModels/UpdateBackend/{backend.Id}", new Dictionary<string, string>
            {
                ["protocol"] = protocol, ["priority"] = "1", ["weight"] = "1",
                ["openAiThinkOverride"] = setting, ["reasoningEffortOverride"] = "high"
            }, $"/VirtualModels/Edit/{model.Id}");
            Assert.AreEqual(HttpStatusCode.Found, updated.StatusCode);
            var saved = await db.VirtualModelBackends.AsNoTracking().SingleAsync(b => b.Id == backend.Id);
            Assert.AreEqual(protocol == "0" ? null : expected, saved.OpenAiThinkOverride);
            Assert.AreEqual(protocol == "0" ? null : "high", saved.ReasoningEffortOverride);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("true")]
    [DataRow("false")]
    public async Task BackendThinking_CreateAddUpdateAndProtocolSwitch(string setting)
    {
        await LoginAsAdmin();
        var modelName = $"thinking-{Guid.NewGuid():N}:latest";
        int providerId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider { Name = "Thinking provider", BaseUrl = "http://localhost:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
        }
        var created = await PostForm("/VirtualModels/Create", new Dictionary<string, string>
        {
            ["Name"] = modelName, ["ProviderId"] = providerId.ToString(),
            ["UnderlyingModel"] = "physical-one", ["Protocol"] = "0", ["OllamaThinkOverride"] = setting
        });
        Assert.AreEqual(HttpStatusCode.Found, created.StatusCode);
        int modelId;
        int backendId;
        bool? expected = setting == "" ? null : bool.Parse(setting);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var model = await db.VirtualModels.Include(m => m.VirtualModelBackends).SingleAsync(m => m.Name == modelName);
            modelId = model.Id;
            backendId = model.VirtualModelBackends.Single().Id;
            Assert.AreEqual(expected, model.VirtualModelBackends.Single().OllamaThinkOverride);
        }
        var page = await Http.GetStringAsync($"/VirtualModels/Edit/{modelId}");
        Assert.IsFalse(page.Contains("name=\"Thinking\""));
        Assert.IsTrue(page.Contains("name=\"ollamaThinkOverride\""));
        var added = await PostForm($"/VirtualModels/AddBackend/{modelId}", new Dictionary<string, string>
        {
            ["providerId"] = providerId.ToString(), ["underlyingModel"] = "physical-two",
            ["protocol"] = "0", ["ollamaThinkOverride"] = setting
        }, tokenUrl: $"/VirtualModels/Edit/{modelId}");
        Assert.AreEqual(HttpStatusCode.Found, added.StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(expected, (await db.VirtualModelBackends.SingleAsync(b => b.VirtualModelId == modelId && b.UnderlyingModelName == "physical-two")).OllamaThinkOverride);
        }
        foreach (var protocol in new[] { "0", "1" })
        {
            var updated = await PostForm($"/VirtualModels/UpdateBackend/{backendId}", new Dictionary<string, string>
            {
                ["protocol"] = protocol, ["priority"] = "2", ["weight"] = "3", ["ollamaThinkOverride"] = setting
            }, tokenUrl: $"/VirtualModels/Edit/{modelId}");
            Assert.AreEqual(HttpStatusCode.Found, updated.StatusCode);
            using var scope = Server!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var backend = await db.VirtualModelBackends.FindAsync(backendId);
            Assert.AreEqual(protocol == "0" ? expected : null, backend!.OllamaThinkOverride);
            Assert.AreEqual(2, backend.Priority);
            Assert.AreEqual(3, backend.Weight);
        }
    }

    [TestMethod]
    [DataRow("1", "medium")]
    [DataRow("2", "future-model-level")]
    [DataRow("1", "")]
    [DataRow("2", "  ")]
    public async Task BackendEffort_CreateAddUpdateAndClear(string protocol, string setting)
    {
        await LoginAsAdmin();
        var name = $"effort-{Guid.NewGuid():N}:latest";
        int providerId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider { Name = "Effort provider", BaseUrl = "https://example.test", ProviderType = ProviderType.OpenAI };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
        }
        var created = await PostForm("/VirtualModels/Create", new Dictionary<string, string>
        {
            ["Name"] = name, ["ProviderId"] = providerId.ToString(), ["UnderlyingModel"] = "one",
            ["Protocol"] = protocol, ["ReasoningEffortOverride"] = setting
        });
        Assert.AreEqual(HttpStatusCode.Found, created.StatusCode);
        int modelId;
        int backendId;
        var expected = string.IsNullOrWhiteSpace(setting) ? null : setting;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var model = await db.VirtualModels.Include(m => m.VirtualModelBackends).SingleAsync(m => m.Name == name);
            modelId = model.Id;
            backendId = model.VirtualModelBackends.Single().Id;
            Assert.AreEqual(expected, model.VirtualModelBackends.Single().ReasoningEffortOverride);
        }
        var added = await PostForm($"/VirtualModels/AddBackend/{modelId}", new Dictionary<string, string>
        {
            ["providerId"] = providerId.ToString(), ["underlyingModel"] = "two", ["protocol"] = protocol,
            ["reasoningEffortOverride"] = setting
        }, tokenUrl: $"/VirtualModels/Edit/{modelId}");
        Assert.AreEqual(HttpStatusCode.Found, added.StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(expected, (await db.VirtualModelBackends.SingleAsync(b => b.VirtualModelId == modelId && b.UnderlyingModelName == "two")).ReasoningEffortOverride);
        }
        foreach (var target in new[] { "1", "2", "0" })
        {
            var updated = await PostForm($"/VirtualModels/UpdateBackend/{backendId}", new Dictionary<string, string>
            {
                ["protocol"] = target, ["priority"] = "1", ["weight"] = "1", ["reasoningEffortOverride"] = " xhigh "
            }, tokenUrl: $"/VirtualModels/Edit/{modelId}");
            Assert.AreEqual(HttpStatusCode.Found, updated.StatusCode);
            using var scope = Server!.Services.CreateScope();
            var backend = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().VirtualModelBackends.FindAsync(backendId);
            Assert.AreEqual(target == "0" ? null : "xhigh", backend!.ReasoningEffortOverride);
        }
    }

    [TestMethod]
    public async Task GetIndex_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/VirtualModels/Index";

        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Chat Models"));
    }

    [TestMethod]
    public async Task GetEmbeddingIndex_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/VirtualModels/EmbeddingIndex";

        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Embedding Models"));
    }

    [TestMethod]
    public async Task GetCreate_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/VirtualModels/Create";

        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task AddBackend_EmptyModel_ShouldNot500()
    {
        await LoginAsAdmin();

        int providerId;
        int vmId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();

            // 1. Create a provider
            var provider = new OllamaProvider
            {
                Name = "Test Provider",
                BaseUrl = "http://localhost:11434"
            };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;

            // 2. Create a virtual model
            var vm = new VirtualModel
            {
                Name = "test:model",
                Type = ModelType.Chat,
                SelectionStrategy = SelectionStrategy.PriorityFallback
            };
            db.VirtualModels.Add(vm);
            await db.SaveChangesAsync();
            vmId = vm.Id;
        }

        // 3. Try to add a backend with EMPTY underlying model
        var addBackendResponse = await PostForm($"/VirtualModels/AddBackend/{vmId}", new Dictionary<string, string>
        {
            { "providerId", providerId.ToString() },
            { "underlyingModel", "" }, // Empty model string
            { "priority", "1" },
            { "weight", "1" }
        }, tokenUrl: $"/VirtualModels/Edit/{vmId}");

        // If it returns 500, it might be redirected to /Error/Code500 or return 500
        if (addBackendResponse.StatusCode == HttpStatusCode.Found)
        {
            var location = addBackendResponse.Headers.Location?.ToString();
            Assert.IsFalse(location?.Contains("Error/Code500") ?? false, "Redirected to 500 error page!");
        }
        else
        {
            Assert.AreNotEqual(HttpStatusCode.InternalServerError, addBackendResponse.StatusCode, "AddBackend returned 500 when underlyingModel was empty!");
        }
    }
}
