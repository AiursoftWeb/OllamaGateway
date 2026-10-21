using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.OllamaGateway.Services.Monitoring;

public sealed class BackendMonitoringService(BackendMonitor monitor, BackendMonitoringSettings settings,
    TemplateDbContext db, IMonitoringIncidentClient client, TimeProvider clock, ILogger<BackendMonitoringService> logger) : IBackgroundJob
{
    public const string Source = "OllamaGateway.BackendMonitoring";
    public string Name => "Backend AlertCenter Monitoring";
    public string Description => "Synchronizes physical backend failures and continuous unavailability with AlertCenter.";

    public async Task ExecuteAsync()
    {
        await monitor.Gate.WaitAsync();
        try
        {
            var config = await settings.LoadAsync();
            if (!config.Enabled) return;
            config.Validate();
            var enabled = (await db.VirtualModelBackends.AsNoTracking().Where(backend => backend.Enabled)
                    .Select(backend => new { backend.ProviderId, backend.UnderlyingModelName }).ToListAsync())
                .Select(backend => new PhysicalModelKey(backend.ProviderId, backend.UnderlyingModelName)).ToHashSet();
            monitor.Retain(enabled);
            var incidents = await LoadIncidentsAsync();
            var snapshots = monitor.Snapshot(config.Rules).ToDictionary(state => state.Key.ToString());
            var included = enabled.Select(key => key.ToString()).ToHashSet(StringComparer.Ordinal);
            var owned = incidents.Where(item => item.Metadata.Value<string>("source") == Source)
                .ToLookup(item => item.Metadata.Value<string>("modelKey"));

            foreach (var key in included.Concat(owned.Select(group => group.Key).OfType<string>()).Distinct())
            {
                try
                {
                    var active = owned[key].OrderBy(item => item.ReceivedAtUtc).ThenBy(item => item.IncidentId).ToList();
                    if (!included.Contains(key))
                    {
                        if (config.AutoMitigate)
                            foreach (var incident in active) await MitigateAsync(incident, "The physical model is no longer enabled for routing in OllamaGateway.");
                        continue;
                    }
                    if (!snapshots.TryGetValue(key, out var snapshot)) continue;
                    // ReportId survives an uncertain create response and is advanced only after confirmation.
                    if (active.Any(item => item.ReportId == snapshot.NextReportId)) monitor.AdvanceReceipt(snapshot.Key, snapshot.NextReportId);
                    snapshot = monitor.Snapshot(snapshot.Key, config.Rules);
                    if (snapshot == null) continue;
                    if (snapshot.MatchingRule == null)
                    {
                        if (config.AutoMitigate && snapshot.CanRecover)
                            foreach (var incident in active) await MitigateAsync(incident,
                                "No monitoring rule matches. A fresh healthy model-directory check and a complete post-start failure window confirm recovery. Final resolution remains manual.");
                        continue;
                    }
                    var rule = snapshot.MatchingRule;
                    if (active.Count == 0)
                    {
                        var title = $"{snapshot.ProviderName} / {snapshot.Key.Model}: backend model unhealthy";
                        await client.CreateAsync(new CreateIncidentRequest
                        {
                            ReportId = snapshot.NextReportId, Title = title[..Math.Min(title.Length, 200)],
                            Level = rule.Severity, OccurredAtUtc = rule.Kind == "offline"
                                ? snapshot.OfflineSince!.Value : snapshot.LastFailure ?? clock.GetUtcNow(),
                            Content = Describe(snapshot, rule, config),
                            Metadata = new JObject { ["source"] = Source, ["modelKey"] = key,
                                ["provider"] = snapshot.ProviderName, ["model"] = snapshot.Key.Model }
                        });
                        monitor.AdvanceReceipt(snapshot.Key, snapshot.NextReportId);
                    }
                    else
                    {
                        // Escalate the same incident. Falling below a higher threshold does not emit another email.
                        var incident = active[0];
                        if (string.CompareOrdinal(rule.Severity, incident.Level) < 0)
                            await client.ChangeSeverityAsync(incident.IncidentId, new ChangeIncidentSeverityRequest
                            {
                                Version = incident.Version, Level = rule.Severity,
                                Reason = Describe(snapshot, rule, config)
                            });
                        foreach (var duplicate in active.Skip(1))
                            await MitigateAsync(duplicate, $"Duplicate physical-model incident. Continue response in incident {incident.IncidentId}.");
                    }
                }
                catch (Exception ex) { logger.LogError(ex, "Backend monitoring synchronization failed for {ModelKey}; retained state for retry", key); }
            }
        }
        catch (Exception ex) { logger.LogError(ex, "Backend monitoring pass failed; retained state for retry"); }
        finally { monitor.Gate.Release(); }
    }

    private async Task MitigateAsync(IncidentDetails incident, string reason) =>
        await client.MitigateAsync(incident.IncidentId, new MitigateIncidentRequest { Version = incident.Version, Reason = reason });

    private async Task<List<IncidentDetails>> LoadIncidentsAsync()
    {
        var results = new List<IncidentDetails>();
        int? total = null;
        for (var page = 1; ; page++)
        {
            var response = await client.QueryAsync(new QueryIncidentsRequest
            {
                ActiveOnly = true, MetadataKey = "source", MetadataValue = Source, Page = page, PageSize = 100
            });
            total ??= response.TotalCount;
            if (response.TotalCount != total || response.Page != page || response.PageSize != 100)
                throw new InvalidOperationException("Incident pagination changed; retry from a fresh snapshot.");
            results.AddRange(response.Items);
            if (results.Count >= total)
            {
                if (results.Count != total || results.Select(item => item.IncidentId).Distinct().Count() != results.Count)
                    throw new InvalidOperationException("Incident pages overlap or have inconsistent totals.");
                return results;
            }
            if (response.Items.Count == 0) throw new InvalidOperationException("Incident listing is incomplete.");
        }
    }

    private string Describe(BackendSnapshot state, BackendMonitoringRule rule, MonitoringConfiguration config)
    {
        var trigger = rule.Kind == "failures"
            ? $"The configured rule triggers {rule.Severity} after more than {rule.FailureCount} failed backend attempts within {rule.Minutes} minutes."
            : $"The model has continuously failed model-directory availability checks since {state.OfflineSince:O}. The configured rule triggers {rule.Severity} after {rule.Minutes} minutes.";
        return $"Physical backend: {state.ProviderName} / {state.Key.Model} (provider ID {state.Key.ProviderId}).\n" +
            trigger + $"\nFailures in the last ten minutes: {state.FailuresInTenMinutes}. Last failed attempt (UTC): {state.LastFailure:O}.\n" +
            $"Last health observation (UTC): {state.LastHealth:O}. Observed at (UTC): {clock.GetUtcNow():O}.\n\n" +
            "Failures include upstream 5xx, authentication rejection, rate limiting, connection errors, backend timeouts and interrupted upstream reads. Client cancellation and ordinary invalid client requests are excluded. " +
            "Retries count as actual attempts, even if another backend ultimately serves the request. Counters use one-second buckets.\n\n" +
            "A model-directory check confirms reachability and model presence, not successful inference. An unloaded model is not offline. " +
            "Other fallback backends may still be serving traffic; this incident alone does not establish a gateway-wide outage.\n\n" +
            "Next steps: Open the provider configuration, check the provider's connectivity, credentials, quota, running model service and recent failed requests. " +
            "Verify a representative inference request through an authorized client. Do not copy API keys or prompt contents into incident comments.\n" +
            $"Provider: {config.PublicBaseUrl.TrimEnd('/')}/Providers/Edit/{state.Key.ProviderId}\n" +
            (config.AutoMitigate ? "Recovery: Automatically mitigate after no rule matches and fresh health confirms recovery; resolve manually."
                : "Recovery: Automatic mitigation is disabled; mitigate and resolve manually after verification.");
    }
}
