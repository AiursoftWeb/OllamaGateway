using System.Net;
using System.Text.Json;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Configuration;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Moq;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

/// <summary>
/// Regression tests for the infinite-recursion bug caused by self-referential Ollama providers.
///
/// Scenario that triggered the bug:
///   1. Provider A  = deepseek (OpenAI type) → virtual model "my-deepseek:latest"
///   2. Provider B  = localhost:5000 (Ollama type, pointing at THIS gateway)
///      — the gateway then exposes "my-deepseek:latest" via /api/tags
///   3. A second virtual model "chat2:latest" backed by Provider B / "my-deepseek:latest"
///
/// When /api/tags or /api/ps was requested, the old code called
/// GetDetailedModelsAsync / GetRunningModelsAsync on every Ollama-type backend, which would
/// issue HTTP GET localhost:5000/api/tags → hitting itself again → infinite recursion →
/// connection pool exhaustion (observed as "SQL infinite recursion").
///
/// These tests verify that /api/tags and /api/ps NEVER call any upstream provider method,
/// so self-referential providers are inherently safe.
/// </summary>
[TestClass]
public class SelfReferentialProviderTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        // Reset the mock — no upstream calls should be configured at all
        TestStartup.MockOllamaService.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        // Allow anonymous API access so tests don't need to log in
        using var scope = Server.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(SettingsMap.AllowAnonymousApiCall, "True");
    }

    private async Task SeedSelfReferentialSetupAsync(int selfPort)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();

        // Provider A: upstream DeepSeek (OpenAI type)
        var deepseekProvider = new OllamaProvider
        {
            Name = "deepseek",
            BaseUrl = "https://api.deepseek.com",
            ProviderType = ProviderType.OpenAI
        };
        db.OllamaProviders.Add(deepseekProvider);
        await db.SaveChangesAsync();

        // Virtual model backed by Provider A
        var myDeepseek = new VirtualModel { Name = "my-deepseek:latest", Type = ModelType.Chat };
        myDeepseek.VirtualModelBackends.Add(new VirtualModelBackend
        {
            ProviderId = deepseekProvider.Id,
            UnderlyingModelName = "deepseek-chat",
            Enabled = true,
            IsHealthy = true
        });
        db.VirtualModels.Add(myDeepseek);
        await db.SaveChangesAsync();

        // Provider B: this gateway itself (Ollama type — the self-referential case)
        var selfProvider = new OllamaProvider
        {
            Name = "self",
            BaseUrl = $"http://localhost:{selfPort}",
            ProviderType = ProviderType.Ollama
        };
        db.OllamaProviders.Add(selfProvider);
        await db.SaveChangesAsync();

        // Virtual model backed by Provider B using the name exposed by /api/tags
        var chat2 = new VirtualModel { Name = "chat2:latest", Type = ModelType.Chat };
        chat2.VirtualModelBackends.Add(new VirtualModelBackend
        {
            ProviderId = selfProvider.Id,
            UnderlyingModelName = "my-deepseek:latest",
            Enabled = true,
            IsHealthy = true
        });
        db.VirtualModels.Add(chat2);
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task Tags_WithSelfReferentialProvider_ReturnsAllVirtualModels()
    {
        await SeedSelfReferentialSetupAsync(Port);

        var response = await Http.GetAsync("/api/tags");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();

        CollectionAssert.Contains(models, "my-deepseek:latest");
        CollectionAssert.Contains(models, "chat2:latest");
    }

    [TestMethod]
    public async Task Tags_WithSelfReferentialProvider_NeverCallsUpstreamProvider()
    {
        await SeedSelfReferentialSetupAsync(Port);

        await Http.GetAsync("/api/tags");

        // If this is called, the old recursive code path is still present
        TestStartup.MockOllamaService.Verify(
            s => s.GetDetailedModelsAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never,
            "GET /api/tags must not call GetDetailedModelsAsync on any upstream provider. " +
            "Doing so causes infinite recursion when a provider points back at this gateway.");
    }

    [TestMethod]
    public async Task Ps_WithSelfReferentialProvider_ReturnsAllVirtualModels()
    {
        await SeedSelfReferentialSetupAsync(Port);

        var response = await Http.GetAsync("/api/ps");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        var models = json.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();

        CollectionAssert.Contains(models, "my-deepseek:latest");
        CollectionAssert.Contains(models, "chat2:latest");
    }

    [TestMethod]
    public async Task Ps_WithSelfReferentialProvider_NeverCallsUpstreamProvider()
    {
        await SeedSelfReferentialSetupAsync(Port);

        await Http.GetAsync("/api/ps");

        // If this is called, the old recursive code path is still present
        TestStartup.MockOllamaService.Verify(
            s => s.GetRunningModelsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()),
            Times.Never,
            "GET /api/ps must not call GetRunningModelsAsync on any upstream provider. " +
            "Doing so causes infinite recursion when a provider points back at this gateway.");
    }

    [TestMethod]
    public async Task Tags_ResponseHasSnakeCaseProperties()
    {
        await SeedSelfReferentialSetupAsync(Port);

        var response = await Http.GetAsync("/api/tags");
        var content = await response.Content.ReadAsStringAsync();

        Assert.IsTrue(content.Contains("\"name\""),        "Expected snake_case 'name' property");
        Assert.IsTrue(content.Contains("\"modified_at\""), "Expected snake_case 'modified_at' property");
        Assert.IsFalse(content.Contains("\"Name\""),       "Must not contain PascalCase 'Name'");
        Assert.IsFalse(content.Contains("\"ModifiedAt\""), "Must not contain PascalCase 'ModifiedAt'");
    }
}
