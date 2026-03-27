using System.Net;
using System.Text.Json;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Aiursoft.WebTools.Extends;
using Moq;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class OllamaApiCompatibilityTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService.Setup(s => s.GetDetailedModelsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<OllamaService.OllamaModel> 
            { 
                new OllamaService.OllamaModel 
                { 
                    Name = "llama3.2", 
                    Model = "llama3.2",
                    Size = 1024,
                    Digest = "sha256:123",
                    Details = new OllamaService.OllamaModelDetails
                    {
                        Format = "gguf",
                        Family = "llama",
                        ParameterSize = "3B",
                        QuantizationLevel = "Q4_K_M"
                    }
                }
            });

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        // Enable anonymous access for easier testing
        using (var scope = Server.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");
        }
    }

    [TestMethod]
    public async Task TestVersionEndpoint()
    {
        // 1. Default version
        var response = await Http.GetAsync("/api/version");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        Assert.IsTrue(json.RootElement.TryGetProperty("version", out var versionProp));
        Assert.AreEqual("0.18.3", versionProp.GetString());

        // 2. Override version via Global Settings
        using (var scope = Server!.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(Aiursoft.OllamaGateway.Configuration.SettingsMap.FakeOllamaVersion, "1.2.3");
        }

        // 3. Request version again -> Should be overridden
        var overriddenResponse = await Http.GetAsync("/api/version");
        Assert.AreEqual(HttpStatusCode.OK, overriddenResponse.StatusCode);
        var overriddenContent = await overriddenResponse.Content.ReadAsStringAsync();
        var overriddenJson = JsonDocument.Parse(overriddenContent);
        Assert.IsTrue(overriddenJson.RootElement.TryGetProperty("version", out var overriddenVersionProp));
        Assert.AreEqual("1.2.3", overriddenVersionProp.GetString());
    }

    [TestMethod]
    public async Task TestTagsCasing()
    {
        // Setup a virtual model
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider { Name = "Default", BaseUrl = "http://localhost:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            
            var virtualModel = new VirtualModel 
            { 
                Name = "my-model", 
                Type = ModelType.Chat
            };
            virtualModel.VirtualModelBackends.Add(new VirtualModelBackend
            {
                ProviderId = provider.Id,
                UnderlyingModelName = "llama3.2",
                Enabled = true,
                IsHealthy = true
            });
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }

        var response = await Http.GetAsync("/api/tags");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        
        // Check for snake_case properties in the JSON string
        Assert.Contains("\"name\"", content);
        Assert.Contains("\"modified_at\"", content);
        Assert.Contains("\"parameter_size\"", content);
        Assert.Contains("\"quantization_level\"", content);
        
        // Ensure NO PascalCase properties of OllamaModel are present in the serialized output
        Assert.IsFalse(content.Contains("\"Name\""));
        Assert.IsFalse(content.Contains("\"ModifiedAt\""));
        Assert.IsFalse(content.Contains("\"ParameterSize\""));
    }
}
