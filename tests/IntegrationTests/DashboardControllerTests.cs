namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class DashboardControllerTests : TestBase
{
    [TestMethod]
    public async Task GetIndex_NotAuthenticated_RedirectsToLogin()
    {
        var url = "/Dashboard/Index";
        
        var response = await Http.GetAsync(url);
        
        // Assert
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }

    [TestMethod]
    public async Task GetIndex_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/Dashboard/Index";
        
        var response = await Http.GetAsync(url);
        
        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Admin Center"));
    }

    [TestMethod]
    public async Task GetMonitor_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/Dashboard/Monitor";
        
        var response = await Http.GetAsync(url);
        
        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Traffic Visualization"));
        Assert.IsTrue(content.Contains("mermaid"));
    }
}
