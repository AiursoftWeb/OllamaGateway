using Aiursoft.OllamaGateway.Configuration;
using Aiursoft.OllamaGateway.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class GlobalSettingsIntegrationTests : TestBase
{
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

    [TestMethod]
    public async Task TestRequestTimeout()
    {
        var settingsService = GetService<GlobalSettingsService>();
        
        await settingsService.UpdateSettingAsync(SettingsMap.RequestTimeoutInMinutes, "15");
        var timeout = await settingsService.GetRequestTimeoutAsync();
        Assert.AreEqual(TimeSpan.FromMinutes(15), timeout);
    }
}
