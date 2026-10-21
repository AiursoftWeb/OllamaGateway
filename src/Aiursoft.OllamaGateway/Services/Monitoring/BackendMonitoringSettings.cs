using Aiursoft.OllamaGateway.Configuration;

namespace Aiursoft.OllamaGateway.Services.Monitoring;

public sealed class BackendMonitoringSettings(GlobalSettingsService settings)
{
    public async Task<MonitoringConfiguration> LoadAsync() => new(
        await settings.GetBoolSettingAsync(SettingsMap.AlertCenterEnabled),
        await settings.GetSettingValueAsync(SettingsMap.AlertCenterEndpoint),
        await settings.GetSettingValueAsync(SettingsMap.AlertCenterApiKey),
        await settings.GetSettingValueAsync(SettingsMap.AlertCenterPublicBaseUrl),
        await settings.GetBoolSettingAsync(SettingsMap.AlertCenterAutoMitigate),
        BackendMonitoringRule.Parse(await settings.GetSettingValueAsync(SettingsMap.AlertCenterRules)));
}

public sealed record MonitoringConfiguration(bool Enabled, string Endpoint, string ApiKey, string PublicBaseUrl,
    bool AutoMitigate, List<BackendMonitoringRule> Rules)
{
    public void Validate()
    {
        foreach (var value in new[] { Endpoint, PublicBaseUrl })
            if (value.Length > 1024 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Use HTTP(S) base URLs without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new InvalidOperationException("An AlertCenter application API key is required.");
    }
}
