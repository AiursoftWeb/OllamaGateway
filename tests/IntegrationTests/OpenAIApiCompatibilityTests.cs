using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Configuration;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class OpenAIApiCompatibilityTests : TestBase
{
    private const string TestModelName = "gpt-3.5-turbo";

    [TestInitialize]
    public override async Task CreateServer()
    {
        await base.CreateServer();

        // Enable anonymous access for easier testing of the API
        using (var scope = Server!.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(SettingsMap.AllowAnonymousApiCall, "True");

            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var virtualModel = new VirtualModel 
            { 
                Name = TestModelName, 
                Type = ModelType.Chat,
                CreatedAt = DateTime.UtcNow
            };
            db.VirtualModels.Add(virtualModel);
            await db.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task TestListModels()
    {
        var response = await Http.GetAsync("/v1/models");
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = System.Text.Json.JsonDocument.Parse(content);
        
        Assert.AreEqual("list", json.RootElement.GetProperty("object").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.IsTrue(data.GetArrayLength() >= 1);
        
        bool found = false;
        foreach (var model in data.EnumerateArray())
        {
            if (model.GetProperty("id").GetString() == TestModelName)
            {
                found = true;
                Assert.AreEqual("model", model.GetProperty("object").GetString());
                Assert.AreEqual("library", model.GetProperty("owned_by").GetString());
                break;
            }
        }
        Assert.IsTrue(found, $"Model {TestModelName} should be in the list");
    }

    [TestMethod]
    public async Task TestGetSingleModel()
    {
        var response = await Http.GetAsync($"/v1/models/{TestModelName}");
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = System.Text.Json.JsonDocument.Parse(content);
        
        Assert.AreEqual(TestModelName, json.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("model", json.RootElement.GetProperty("object").GetString());
        Assert.AreEqual("library", json.RootElement.GetProperty("owned_by").GetString());
        Assert.IsTrue(json.RootElement.TryGetProperty("created", out _));
    }

    [TestMethod]
    public async Task TestGetNonExistentModel()
    {
        var response = await Http.GetAsync("/v1/models/non-existent-model");
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = System.Text.Json.JsonDocument.Parse(content);
        
        Assert.IsTrue(json.RootElement.TryGetProperty("error", out var error));
        Assert.AreEqual("model_not_found", error.GetProperty("code").GetString());
    }
}
