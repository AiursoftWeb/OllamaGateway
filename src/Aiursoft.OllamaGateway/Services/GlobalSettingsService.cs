using Aiursoft.Scanner.Abstractions;
using Aiursoft.OllamaGateway.Configuration;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Aiursoft.OllamaGateway.Services;

public class GlobalSettingsService(
    TemplateDbContext dbContext, 
    IConfiguration configuration,
    IMemoryCache cache) : IScopedDependency
{
    private string GetCacheKey(string key) => $"global-setting-{key}";

    public async Task<string> GetSettingValueAsync(string key)
    {
        var cacheKey = GetCacheKey(key);
        if (cache.TryGetValue(cacheKey, out string? cachedValue) && cachedValue != null)
        {
            return cachedValue;
        }

        // 1. Check configuration (Environment variables, appsettings.json, etc.)
        var configValue = configuration[$"GlobalSettings:{key}"] ?? configuration[key];
        if (!string.IsNullOrWhiteSpace(configValue))
        {
            return configValue;
        }

        // 2. Check database
        var dbSetting = await dbContext.GlobalSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        string result;
        if (dbSetting != null && dbSetting.Value != null)
        {
            result = dbSetting.Value;
        }
        else
        {
            // 3. Fallback to default
            var definition = SettingsMap.Definitions.FirstOrDefault(d => d.Key == key);
            result = definition?.DefaultValue ?? string.Empty;
        }

        cache.Set(cacheKey, result, TimeSpan.FromHours(2));
        return result;
    }

    public async Task<bool> GetBoolSettingAsync(string key)
    {
        var value = await GetSettingValueAsync(key);
        return bool.TryParse(value, out var result) && result;
    }

    // ReSharper disable once UnusedMember.Global
    public async Task<int> GetIntSettingAsync(string key)
    {
        var value = await GetSettingValueAsync(key);
        return int.TryParse(value, out var result) ? result : 0;
    }

    public bool IsOverriddenByConfig(string key)
    {
        return !string.IsNullOrWhiteSpace(configuration[$"GlobalSettings:{key}"]) ||
               !string.IsNullOrWhiteSpace(configuration[key]);
    }

    public async Task UpdateSettingAsync(string key, string value)
    {
        if (IsOverriddenByConfig(key))
        {
            throw new InvalidOperationException($"Setting {key} is overridden by configuration and cannot be updated in database.");
        }

        var definition = SettingsMap.Definitions.FirstOrDefault(d => d.Key == key)
                         ?? throw new InvalidOperationException($"Setting {key} is not defined.");

        // Validation
        switch (definition.Type)
        {
            case SettingType.Bool:
                if (!bool.TryParse(value, out _))
                {
                    throw new InvalidOperationException($"Value '{value}' is not a valid boolean for setting {key}.");
                }
                break;
            case SettingType.Number:
                if (!double.TryParse(value, out _))
                {
                    throw new InvalidOperationException($"Value '{value}' is not a valid number for setting {key}.");
                }
                break;
            case SettingType.Choice:
                if (definition.ChoiceOptions != null && !definition.ChoiceOptions.ContainsKey(value))
                {
                    throw new InvalidOperationException($"Value '{value}' is not a valid choice for setting {key}.");
                }
                break;
        }

        var dbSetting = await dbContext.GlobalSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (dbSetting == null)
        {
            dbSetting = new GlobalSetting { Key = key };
            dbContext.GlobalSettings.Add(dbSetting);
        }

        dbSetting.Value = value;
        await dbContext.SaveChangesAsync();
        cache.Remove(GetCacheKey(key));
    }

    public async Task<string> GetProjectNameAsync() => await GetSettingValueAsync(SettingsMap.ProjectName);
    public async Task<string> GetBrandNameAsync() => await GetSettingValueAsync(SettingsMap.BrandName);
    public async Task<string> GetBrandHomeUrlAsync() => await GetSettingValueAsync(SettingsMap.BrandHomeUrl);
    public async Task<string> GetProjectLogoAsync() => await GetSettingValueAsync(SettingsMap.ProjectLogo);
    public async Task<bool> GetAllowUserAdjustNicknameAsync() => await GetBoolSettingAsync(SettingsMap.AllowUserAdjustNickname);
    public async Task<string> GetIcpAsync() => await GetSettingValueAsync(SettingsMap.Icp);
    public async Task<string> GetDefaultChatModelAsync() => await GetSettingValueAsync(SettingsMap.DefaultChatModel);
    public async Task<string> GetDefaultEmbeddingModelAsync() => await GetSettingValueAsync(SettingsMap.DefaultEmbeddingModel);
    public async Task<bool> GetAllowAnonymousApiCallAsync() => await GetBoolSettingAsync(SettingsMap.AllowAnonymousApiCall);
    public async Task<TimeSpan> GetRequestTimeoutAsync()
    {
        var minutesStr = await GetSettingValueAsync(SettingsMap.RequestTimeoutInMinutes);
        if (int.TryParse(minutesStr, out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }
        return TimeSpan.FromMinutes(10);
    }

    public async Task SetProjectNameAsync(string value) => await UpdateSettingAsync(SettingsMap.ProjectName, value);
    public async Task SetBrandNameAsync(string value) => await UpdateSettingAsync(SettingsMap.BrandName, value);
    public async Task SetBrandHomeUrlAsync(string value) => await UpdateSettingAsync(SettingsMap.BrandHomeUrl, value);
    public async Task SetProjectLogoAsync(string value) => await UpdateSettingAsync(SettingsMap.ProjectLogo, value);
    public async Task SetAllowUserAdjustNicknameAsync(bool value) => await UpdateSettingAsync(SettingsMap.AllowUserAdjustNickname, value.ToString());
    public async Task SetIcpAsync(string value) => await UpdateSettingAsync(SettingsMap.Icp, value);
    public async Task SetDefaultChatModelAsync(string value) => await UpdateSettingAsync(SettingsMap.DefaultChatModel, value);
    public async Task SetDefaultEmbeddingModelAsync(string value) => await UpdateSettingAsync(SettingsMap.DefaultEmbeddingModel, value);

    public async Task InitializeSettingsAsync()
    {
        foreach (var definition in SettingsMap.Definitions)
        {
            var dbSetting = await dbContext.GlobalSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == definition.Key);
            if (dbSetting == null)
            {
                var initialValue = configuration[$"GlobalSettings:{definition.Key}"] 
                                   ?? configuration[definition.Key]
                                   ?? definition.DefaultValue;
                dbContext.GlobalSettings.Add(new GlobalSetting
                {
                    Key = definition.Key,
                    Value = initialValue
                });
                cache.Remove(GetCacheKey(definition.Key));
            }
        }
        await dbContext.SaveChangesAsync();
    }
}
