using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Configuration;
using Aiursoft.OllamaGateway.Models.Monitoring;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Monitoring;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Aiursoft.OllamaGateway.Controllers;

[Authorize(Policy = AppPermissionNames.CanManageAlertCenterIntegration)]
[LimitPerMin]
public sealed class AlertMonitoringController(GlobalSettingsService settings, BackendMonitor monitor,
    IStringLocalizer<AlertMonitoringController> localizer) : Controller
{
    private static string Version(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [RenderInNavBar(NavGroupName = "Features", NavGroupOrder = 1, CascadedLinksGroupName = "Monitoring",
        CascadedLinksIcon = "bell", CascadedLinksOrder = 2, LinkText = "AlertCenter monitoring", LinkOrder = 1)]
    public async Task<IActionResult> Index()
    {
        var json = await settings.GetSettingValueAsync(SettingsMap.AlertCenterRules);
        var model = new MonitoringViewModel
        {
            Version = Version(json), RulesReadOnly = settings.IsOverriddenByConfig(SettingsMap.AlertCenterRules),
            AutoMitigateReadOnly = settings.IsOverriddenByConfig(SettingsMap.AlertCenterAutoMitigate),
            Enabled = await settings.GetBoolSettingAsync(SettingsMap.AlertCenterEnabled),
            ApiKeyConfigured = !string.IsNullOrWhiteSpace(await settings.GetSettingValueAsync(SettingsMap.AlertCenterApiKey)),
            AutoMitigate = await settings.GetBoolSettingAsync(SettingsMap.AlertCenterAutoMitigate)
        };
        try { model.Rules = BackendMonitoringRule.Parse(json); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { model.InvalidRules = true; }
        model.Backends = monitor.Snapshot(model.Rules).OrderBy(state => state.ProviderName).ThenBy(state => state.Key.Model).ToList();
        return this.StackView(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRule(string version, Guid? id, string kind, int minutes, int failureCount, string severity, bool isEnabled)
    {
        var candidate = new BackendMonitoringRule { Kind = kind, Minutes = minutes, FailureCount = failureCount, Severity = severity, IsEnabled = isEnabled };
        if (!ModelState.IsValid || !candidate.IsValid())
            return BadRequest(localizer["Select a valid rule, a window from 1 to 60 minutes, a nonnegative failure threshold and sev0 to sev4."].Value);
        return await UpdateRules(version, rules =>
        {
            if (id.HasValue)
            {
                var index = rules.FindIndex(rule => rule.Id == id);
                if (index < 0) return false;
                candidate.Id = id.Value;
                rules[index] = candidate;
            }
            else rules.Add(candidate);
            return true;
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteRule(string version, Guid id) => UpdateRules(version, rules => rules.RemoveAll(rule => rule.Id == id) > 0);

    private async Task<IActionResult> UpdateRules(string version, Func<List<BackendMonitoringRule>, bool> update)
    {
        await monitor.Gate.WaitAsync();
        try
        {
            var json = await settings.GetSettingValueAsync(SettingsMap.AlertCenterRules);
            if (Version(json) != version) return Conflict(localizer["The monitoring rules changed. Refresh before saving."].Value);
            var rules = BackendMonitoringRule.Parse(json);
            if (!update(rules)) return NotFound();
            var updated = JsonSerializer.Serialize(rules);
            BackendMonitoringRule.Parse(updated);
            await settings.UpdateSettingAsync(SettingsMap.AlertCenterRules, updated);
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Conflict(localizer["The rules are invalid or overridden by configuration. Check Global Settings."].Value);
        }
        finally { monitor.Gate.Release(); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRecovery(bool autoMitigate)
    {
        if (!ModelState.IsValid) return BadRequest();
        try { await settings.UpdateSettingAsync(SettingsMap.AlertCenterAutoMitigate, autoMitigate.ToString()); }
        catch (InvalidOperationException) { return Conflict(localizer["This setting is overridden by configuration."].Value); }
        return RedirectToAction(nameof(Index));
    }
}
