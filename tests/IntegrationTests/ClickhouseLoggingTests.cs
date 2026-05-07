using System.Net;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Microsoft.EntityFrameworkCore;
using static Aiursoft.WebTools.Extends;
using Moq;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class ClickhouseLoggingTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        // Setup mocks before creating server
        TestStartup.MockClickhouse.Reset();
        // ENABLE Clickhouse for these tests
        TestStartup.MockClickhouse.Setup(c => c.Enabled).Returns(true);

        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService.Setup(s => s.GetUnderlyingModelsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<string> { "llama3.2" });
        
        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        // Enable anonymous access for simplicity in testing
        using (var scope = Server.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");
        }
    }

    [TestMethod]
    public async Task TestClickhouseLoggingFilter()
    {
        // 1. Prepare a virtual model
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider { Name = "Provider", BaseUrl = "http://localhost:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            var virtualModel = new VirtualModel 
            { 
                Name = "chat-model", 
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

        // 2. Mock upstream
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) 
        { 
            Content = new StringContent("{\"done\": true, \"model\": \"llama3.2\", \"message\": {\"content\": \"hi\"}}") 
        });

        // 3. Make an AI request
        var aiRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        aiRequest.Content = new StringContent("{\"model\": \"chat-model\"}", System.Text.Encoding.UTF8, "application/json");
        var aiResponse = await Http.SendAsync(aiRequest);
        Assert.AreEqual(HttpStatusCode.OK, aiResponse.StatusCode);

        // Verify it WAS logged to Clickhouse (SaveChangesAsync called)
        TestStartup.MockClickhouse.Verify(c => c.SaveChangesAsync(), Times.Once);

        // 4. Make a non-AI request (Home page)
        var homeResponse = await Http.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, homeResponse.StatusCode);

        // Verify it was NOT logged to Clickhouse
        // Times.Once still, because no new call to SaveChangesAsync should happen.
        TestStartup.MockClickhouse.Verify(c => c.SaveChangesAsync(), Times.Once);
        
        // 5. Make another AI request (OpenAI style)
        var oaiRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        oaiRequest.Content = new StringContent("{\"model\": \"chat-model\", \"messages\":[]}", System.Text.Encoding.UTF8, "application/json");
        var oaiResponse = await Http.SendAsync(oaiRequest);
        Assert.AreEqual(HttpStatusCode.OK, oaiResponse.StatusCode);

        // Verify it WAS logged to Clickhouse
        TestStartup.MockClickhouse.Verify(c => c.SaveChangesAsync(), Times.Exactly(2));
    }
}
