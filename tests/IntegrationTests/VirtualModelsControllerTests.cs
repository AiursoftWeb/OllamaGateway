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
}
