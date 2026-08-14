using Aiursoft.OllamaGateway.Entities;
using Moq;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class OllamaProvidersControllerTests : TestBase
{
    [TestMethod]
    public async Task GetIndex_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/OllamaProviders/Index";

        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Providers"));
    }

    [TestMethod]
    public async Task GetCreate_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/OllamaProviders/Create";

        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("SupportsOpenAiChatCompletions", content);
        Assert.Contains("SupportsOpenAiResponses", content);
        Assert.Contains("/v1/chat/completions", content);
        Assert.Contains("/v1/responses", content);
    }

    [TestMethod]
    public async Task Index_ShowsOpenAiProviderProtocolCapabilitiesAtAGlance()
    {
        await LoginAsAdmin();
        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService
            .Setup(service => service.GetOpenAIAvailableModelsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(["gpt-physical"]);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OllamaProviders.Add(new OllamaProvider
            {
                Name = "Visible dual protocol provider",
                BaseUrl = "https://dual-provider.test",
                ProviderType = ProviderType.OpenAI,
                SupportsOpenAiChatCompletions = true,
                SupportsOpenAiResponses = true
            });
            db.OllamaProviders.Add(new OllamaProvider
            {
                Name = "Visible legacy inferred provider",
                BaseUrl = "https://legacy-inferred.test",
                ProviderType = ProviderType.OpenAI
            });
            await db.SaveChangesAsync();
        }

        var response = await Http.GetAsync("/OllamaProviders/Index");
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Visible dual protocol provider", content);
        Assert.Contains("Protocols", content);
        Assert.Contains("Chat Completions", content);
        Assert.Contains("Responses", content);
        Assert.Contains("Visible legacy inferred provider", content);
        Assert.Contains("Legacy inferred", content);
    }

    [TestMethod]
    public async Task CreateOpenAiProvider_RequiresAtLeastOneGenerationProtocol()
    {
        await LoginAsAdmin();

        var response = await PostForm("/OllamaProviders/Create", new Dictionary<string, string>
        {
            ["Name"] = "Invalid OpenAI provider",
            ["BaseUrl"] = "https://invalid-provider.test",
            ["ProviderType"] = ((int)ProviderType.OpenAI).ToString(),
            ["SupportsOpenAiChatCompletions"] = "false",
            ["SupportsOpenAiResponses"] = "false",
            ["KeepAlive"] = "5m",
            ["HealthCheckTimeoutSeconds"] = "60"
        });

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Select at least one OpenAI generation protocol",
            await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task CreateOpenAiProvider_PersistsBothProtocolCapabilities()
    {
        await LoginAsAdmin();
        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService
            .Setup(service => service.GetOpenAIAvailableModelsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(["gpt-physical"]);

        var response = await PostForm("/OllamaProviders/Create", new Dictionary<string, string>
        {
            ["Name"] = "Persisted dual protocol provider",
            ["BaseUrl"] = "https://persisted-dual.test",
            ["ProviderType"] = ((int)ProviderType.OpenAI).ToString(),
            ["SupportsOpenAiChatCompletions"] = "true",
            ["SupportsOpenAiResponses"] = "true",
            ["KeepAlive"] = "5m",
            ["HealthCheckTimeoutSeconds"] = "60"
        });

        AssertRedirect(response, "/OllamaProviders", exact: false);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var provider = db.OllamaProviders.Single(item => item.Name == "Persisted dual protocol provider");
        Assert.IsTrue(provider.SupportsOpenAiChatCompletions == true);
        Assert.IsTrue(provider.SupportsOpenAiResponses == true);
    }
}
