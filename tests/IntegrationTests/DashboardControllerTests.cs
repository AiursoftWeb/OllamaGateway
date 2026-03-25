namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class DashboardControllerTests : TestBase
{
    [TestMethod]
    public async Task GetIndex_NotAuthenticated_RedirectsToLogin()
    {
        // This is a basic test to ensure the controller is reachable.
        // Adjust the path as necessary for specific controllers.
        var url = "/Dashboard/Index";
        
        var response = await Http.GetAsync(url);
        
        // Assert
        // Now that we added [Authorize], it should redirect to login.
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }
}
