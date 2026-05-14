using System.Net;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class VirtualModelsControllerTests : TestBase
{
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
