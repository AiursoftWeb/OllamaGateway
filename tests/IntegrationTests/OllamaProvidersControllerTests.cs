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
    }
}
