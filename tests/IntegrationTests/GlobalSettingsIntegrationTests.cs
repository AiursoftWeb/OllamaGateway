using Aiursoft.OllamaGateway.Configuration;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.FileStorage;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class GlobalSettingsIntegrationTests : TestBase
{
    [TestMethod]
    [DataRow("")]
    [DataRow("project-logo/missing-logo.png")]
    [DataRow("../outside.png")]
    public async Task InvalidLogoFilesAreRejectedWithoutChangingTheSetting(string path)
    {
        var settings = GetService<GlobalSettingsService>();
        var previous = await settings.GetSettingValueAsync(SettingsMap.ProjectLogo);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            settings.UpdateSettingAsync(SettingsMap.ProjectLogo, path));
        Assert.AreEqual(previous, await settings.GetSettingValueAsync(SettingsMap.ProjectLogo));
    }

    [TestMethod]
    public async Task ExistingLogoFileCanBeSaved()
    {
        var storage = GetService<StorageService>();
        using var image = new MemoryStream([1, 2, 3]);
        var path = await storage.SaveFromStream($"project-logo/{Guid.NewGuid():N}.png", image);
        var settings = GetService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(SettingsMap.ProjectLogo, path);
        Assert.AreEqual(path, await settings.GetSettingValueAsync(SettingsMap.ProjectLogo));
    }

    [TestMethod]
    public async Task TestSettingWorkflow()
    {
        var settingsService = GetService<GlobalSettingsService>();

        // 1. Get default value
        var projectName = await settingsService.GetProjectNameAsync();
        Assert.IsNotNull(projectName);

        // 2. Update value (should update DB and clear cache)
        const string newName = "New Test Project";
        await settingsService.SetProjectNameAsync(newName);

        // 3. Get updated value
        var updatedName = await settingsService.GetProjectNameAsync();
        Assert.AreEqual(newName, updatedName);
    }

    [TestMethod]
    public async Task TestBoolSetting()
    {
        var settingsService = GetService<GlobalSettingsService>();

        await settingsService.SetAllowUserAdjustNicknameAsync(true);
        Assert.IsTrue(await settingsService.GetAllowUserAdjustNicknameAsync());

        await settingsService.SetAllowUserAdjustNicknameAsync(false);
        Assert.IsFalse(await settingsService.GetAllowUserAdjustNicknameAsync());
    }

    [TestMethod]
    public async Task TestUpdateInvalidBool()
    {
        var settingsService = GetService<GlobalSettingsService>();
        try
        {
            await settingsService.UpdateSettingAsync(SettingsMap.AllowUserAdjustNickname, "not-a-bool");
            Assert.Fail("Should have thrown InvalidOperationException");
        }
        catch (InvalidOperationException)
        {
            // Success
        }
    }
}
