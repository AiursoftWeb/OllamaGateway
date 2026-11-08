using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class UnderlyingModelsControllerTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        TestStartup.OllamaStub.Reset();
        await base.CreateServer();
    }

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

    [TestMethod]
    public async Task OpenAiInventory_DoesNotClaimLoadingStatusOrInventMetadata()
    {
        await AddProvider(ProviderType.OpenAI);
        TestStartup.OllamaStub.OpenAIModels = ["remote-model"];
        await LoginAsAdmin();

        var content = await Http.GetStringAsync("/UnderlyingModels");
        var row = content[content.IndexOf("<tbody>", StringComparison.Ordinal)..];

        StringAssert.Contains(row, "remote-model");
        StringAssert.Contains(row, ">Listed</span>");
        StringAssert.Contains(row, ">Not reported</span>");
        Assert.IsFalse(row.Contains("0 GB"));
        Assert.IsFalse(row.Contains("data-utc-time"));
        Assert.IsFalse(row.Contains(">Running</span>"));
        Assert.IsFalse(row.Contains(">Idle</span>"));
        Assert.IsFalse(row.Contains("...</small>"));
    }

    [TestMethod]
    public async Task OllamaInventory_PreservesReportedLoadingStatusAndMetadata()
    {
        await AddProvider(ProviderType.Ollama);
        TestStartup.OllamaStub.DetailedModels =
        [
            new OllamaService.OllamaModel { Name = "loaded-model", Size = 4294967296L, Digest = "0123456789abcdef", ModifiedAt = new DateTime(2026, 11, 1) },
            new OllamaService.OllamaModel { Name = "idle-model", Size = 2147483648L, ModifiedAt = new DateTime(2026, 11, 2) }
        ];
        TestStartup.OllamaStub.RunningModels = [new OllamaService.OllamaRunningModel { Name = "loaded-model" }];
        await LoginAsAdmin();

        var content = await Http.GetStringAsync("/UnderlyingModels");
        var row = content[content.IndexOf("<tbody>", StringComparison.Ordinal)..];

        StringAssert.Contains(row, ">Running</span>");
        StringAssert.Contains(row, ">Idle</span>");
        StringAssert.Contains(row, "4 GB");
        StringAssert.Contains(row, "2 GB");
        StringAssert.Contains(row, "0123456789ab...");
        StringAssert.Contains(row, "data-utc-time");
        Assert.IsFalse(row.Contains(">Unknown</span>"));
        Assert.IsFalse(row.Contains(">Not reported</span>"));
    }

    [TestMethod]
    public async Task OllamaLoadingStatusUnavailable_IsUnknownRatherThanIdle()
    {
        await AddProvider(ProviderType.Ollama);
        TestStartup.OllamaStub.DetailedModels = [new OllamaService.OllamaModel { Name = "listed-model" }];
        TestStartup.OllamaStub.RunningModels = null;
        await LoginAsAdmin();

        var content = await Http.GetStringAsync("/UnderlyingModels");
        var row = content[content.IndexOf("<tbody>", StringComparison.Ordinal)..];

        StringAssert.Contains(row, "listed-model");
        StringAssert.Contains(row, ">Unknown</span>");
        Assert.IsFalse(row.Contains(">Running</span>"));
        Assert.IsFalse(row.Contains(">Idle</span>"));
    }

    private async Task AddProvider(ProviderType type)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.OllamaProviders.Add(new OllamaProvider { Name = "Fixture", BaseUrl = "http://provider.test", ProviderType = type });
        await db.SaveChangesAsync();
    }
}
