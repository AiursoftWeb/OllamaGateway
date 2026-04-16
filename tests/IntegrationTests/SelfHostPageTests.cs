namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class SelfHostPageTests : TestBase
{
    [TestMethod]
    public async Task GetSelfHost()
    {
        var response = await Http.GetAsync("/Home/SelfHost");
        response.EnsureSuccessStatusCode();
    }
}
