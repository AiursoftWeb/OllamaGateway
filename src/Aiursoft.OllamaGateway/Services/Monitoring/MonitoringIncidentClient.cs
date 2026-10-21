using Aiursoft.AiurProtocol.Services;
using Aiursoft.AlertCenter.SDK;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.Extensions.Options;

namespace Aiursoft.OllamaGateway.Services.Monitoring;

public interface IMonitoringIncidentClient
{
    Task<QueryIncidentsResponse> QueryAsync(QueryIncidentsRequest request);
    Task<CreateIncidentResponse> CreateAsync(CreateIncidentRequest request);
    Task<ChangeIncidentSeverityResponse> ChangeSeverityAsync(Guid id, ChangeIncidentSeverityRequest request);
    Task<MitigateIncidentResponse> MitigateAsync(Guid id, MitigateIncidentRequest request);
}

public sealed class MonitoringIncidentClient(AiurProtocolClient http, BackendMonitoringSettings settings) : IMonitoringIncidentClient
{
    private Task<AlertCenterApiClient>? _client;
    private async Task<AlertCenterApiClient> CreateClientAsync()
    {
        var config = await settings.LoadAsync();
        config.Validate();
        return new AlertCenterApiClient(http, Options.Create(new AlertCenterApiOptions { Endpoint = config.Endpoint.TrimEnd('/') }),
            new SnapshotKeyProvider(config.ApiKey));
    }
    private Task<AlertCenterApiClient> GetClientAsync() => _client ??= CreateClientAsync();
    public async Task<QueryIncidentsResponse> QueryAsync(QueryIncidentsRequest request) => await (await GetClientAsync()).QueryIncidentsAsync(request);
    public async Task<CreateIncidentResponse> CreateAsync(CreateIncidentRequest request) => await (await GetClientAsync()).CreateIncidentAsync(request);
    public async Task<ChangeIncidentSeverityResponse> ChangeSeverityAsync(Guid id, ChangeIncidentSeverityRequest request) =>
        await (await GetClientAsync()).ChangeIncidentSeverityAsync(id, request);
    public async Task<MitigateIncidentResponse> MitigateAsync(Guid id, MitigateIncidentRequest request) =>
        await (await GetClientAsync()).MitigateIncidentAsync(id, request);
    private sealed class SnapshotKeyProvider(string key) : IAlertCenterApiKeyProvider
    {
        public ValueTask<string> GetApiKeyAsync() => ValueTask.FromResult(key);
    }
}
