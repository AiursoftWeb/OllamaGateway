namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class UnderlyingModelsControllerTests : TestBase
{
    [TestMethod]
    public async Task GetIndex_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/UnderlyingModels/Index";

        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Physical Models"));
    }
}
