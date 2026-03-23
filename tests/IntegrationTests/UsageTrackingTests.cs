using System.Net;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Microsoft.EntityFrameworkCore;
using static Aiursoft.WebTools.Extends;
using Moq;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class UsageTrackingTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        // Setup mocks before creating server
        TestStartup.MockClickhouse.Reset();
        TestStartup.MockClickhouse.Setup(c => c.Enabled).Returns(false);

        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService.Setup(s => s.GetUnderlyingModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<string> { "llama3.2" });
        
        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();

        // Ensure anonymous access is off 
        using (var scope = Server.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "False");
        }
    }

    [TestMethod]
    public async Task TestUsageTrackingAndFlushing()
    {
        // 1. Setup Data
        string apiKeyStr = "usage-test-token";
        int providerId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.FirstAsync();
            var apiKey = new ApiKey { Name = "Usage Test Key", Key = apiKeyStr, UserId = user.Id };
            db.ApiKeys.Add(apiKey);
            
            var provider = new OllamaProvider { Name = "Usage Provider", BaseUrl = "http://localhost:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;

            var virtualModel = new VirtualModel 
            { 
                Name = "usage-model", 
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

        // 2. Make an API call to trigger usage tracking
        // We need to mock the upstream response for the proxy
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) 
        { 
            Content = new StringContent("{\"done\": true, \"model\": \"llama3.2\", \"message\": {\"content\": \"hi\"}}") 
        });

        var proxyRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/chat");
        proxyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKeyStr);
        proxyRequest.Content = new StringContent("{\"model\": \"usage-model\"}", System.Text.Encoding.UTF8, "application/json");
        var proxyResponse = await Http.SendAsync(proxyRequest);
        Assert.AreEqual(HttpStatusCode.OK, proxyResponse.StatusCode);

        // 3. Verify usage is in BUFFER but NOT yet in DB
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var apiKey = await db.ApiKeys.FirstAsync(k => k.Key == apiKeyStr);
            Assert.AreEqual(0, apiKey.UsageCount, "Usage should not be in DB yet.");
            
            var modelUsage = await db.UnderlyingModelUsages.FirstOrDefaultAsync(u => u.ProviderId == providerId && u.ModelName == "llama3.2");
            Assert.IsNull(modelUsage, "Model usage should not be in DB yet.");
        }

        // 4. Manually trigger flush 
        using (var scope = Server!.Services.CreateScope())
        {
            var usageCounter = scope.ServiceProvider.GetRequiredService<UsageCounter>();
            var dbContext = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var isInMemory = dbContext.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory";
            
            // Manual flush logic (simulating UsageFlushService)
            var (apiKeyUsages, apiKeyLastUsed) = usageCounter.SwapApiKeyBuffers();
            foreach (var id in apiKeyUsages.Keys)
            {
                if (isInMemory)
                {
                    var apiKey = await dbContext.ApiKeys.FindAsync(id);
                    if (apiKey != null)
                    {
                        apiKey.UsageCount += apiKeyUsages[id];
                        apiKey.LastUsed = apiKeyLastUsed[id];
                    }
                }
                else
                {
                    await dbContext.ApiKeys.Where(a => a.Id == id).ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.UsageCount, a => a.UsageCount + apiKeyUsages[id])
                        .SetProperty(a => a.LastUsed, apiKeyLastUsed[id]));
                }
            }
            
            var (modelUsages, modelLastUsed) = usageCounter.SwapModelBuffers();
            foreach (var modelKey in modelUsages.Keys)
            {
                dbContext.UnderlyingModelUsages.Add(new UnderlyingModelUsage
                {
                    ProviderId = modelKey.providerId,
                    ModelName = modelKey.modelName,
                    UsageCount = modelUsages[modelKey],
                    LastUsed = modelLastUsed[modelKey]
                });
            }
            await dbContext.SaveChangesAsync();
        }

        // 5. Verify usage IS now in DB
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var apiKey = await db.ApiKeys.FirstAsync(k => k.Key == apiKeyStr);
            Assert.IsTrue(apiKey.UsageCount >= 1, $"Usage should be in DB now. Actual: {apiKey.UsageCount}");
            
            var modelUsage = await db.UnderlyingModelUsages.FirstOrDefaultAsync(u => u.ProviderId == providerId && u.ModelName == "llama3.2");
            Assert.IsNotNull(modelUsage);
            Assert.IsTrue(modelUsage.UsageCount >= 1);
        }
    }
}
