using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Services.Monitoring;

public readonly record struct PhysicalModelKey(int ProviderId, string Model)
{
    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ProviderId}:{Model}");
}

public sealed class BackendMonitor(TimeProvider clock)
{
    public static readonly TimeSpan HealthFreshness = TimeSpan.FromMinutes(3);
    private readonly object _stateLock = new();
    private readonly Dictionary<PhysicalModelKey, ModelState> _states = new();
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Failure(VirtualModelBackend backend)
    {
        lock (_stateLock)
        {
            var state = Get(backend);
            state.Advance(clock.GetUtcNow().ToUnixTimeSeconds());
            var index = (int)(state.LastSecond % 3600);
            if (state.Buckets[index] < int.MaxValue) state.Buckets[index]++;
            state.LastFailure = clock.GetUtcNow();
        }
    }

    public void Health(VirtualModelBackend backend, bool healthy)
    {
        lock (_stateLock)
        {
            var state = Get(backend);
            var now = clock.GetUtcNow();
            // A gap is unknown time, not evidence of continuous unavailability.
            if (healthy || state.LastHealth == null || now - state.LastHealth > HealthFreshness)
                state.OfflineSince = healthy ? null : now;
            else state.OfflineSince ??= now;
            state.Healthy = healthy;
            state.LastHealth = now;
        }
    }

    private ModelState Get(VirtualModelBackend backend)
    {
        var key = new PhysicalModelKey(backend.ProviderId, backend.UnderlyingModelName);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new ModelState { FirstObserved = clock.GetUtcNow(), Key = key };
            _states.Add(key, state);
        }
        state.ProviderName = backend.Provider?.Name ?? backend.ProviderId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return state;
    }

    public void Retain(IReadOnlySet<PhysicalModelKey> enabled)
    {
        lock (_stateLock)
            foreach (var key in _states.Keys.Where(key => !enabled.Contains(key)).ToList()) _states.Remove(key);
    }

    public List<BackendSnapshot> Snapshot(IReadOnlyList<BackendMonitoringRule> rules)
    {
        lock (_stateLock)
        {
            var now = clock.GetUtcNow();
            return _states.Values.Select(state => Snapshot(state, rules, now)).ToList();
        }
    }

    public BackendSnapshot? Snapshot(PhysicalModelKey key, IReadOnlyList<BackendMonitoringRule> rules)
    {
        lock (_stateLock)
            return _states.TryGetValue(key, out var state) ? Snapshot(state, rules, clock.GetUtcNow()) : null;
    }

    private static BackendSnapshot Snapshot(ModelState state, IReadOnlyList<BackendMonitoringRule> rules, DateTimeOffset now)
    {
        state.Advance(now.ToUnixTimeSeconds());
        var fresh = state.LastHealth.HasValue && now - state.LastHealth <= HealthFreshness;
        var match = rules.Where(rule => rule.IsEnabled && (rule.Kind == "failures"
                ? state.Count(rule.Minutes) > rule.FailureCount
                : fresh && !state.Healthy && state.OfflineSince.HasValue && now - state.OfflineSince >= TimeSpan.FromMinutes(rule.Minutes)))
            .OrderBy(rule => rule.Severity, StringComparer.Ordinal).FirstOrDefault();
        var warmup = rules.Where(rule => rule.IsEnabled && rule.Kind == "failures")
            .Select(rule => rule.Minutes).DefaultIfEmpty(0).Max();
        return new BackendSnapshot(state.Key, state.ProviderName, state.LastHealth,
            state.OfflineSince, state.LastFailure, fresh ? state.Healthy : null, state.Count(10),
            match, state.NextReportId, fresh && state.Healthy && now - state.FirstObserved >= TimeSpan.FromMinutes(warmup));
    }

    public void AdvanceReceipt(PhysicalModelKey key, Guid reportId)
    {
        lock (_stateLock)
            if (_states.TryGetValue(key, out var state) && state.NextReportId == reportId)
                state.NextReportId = Guid.NewGuid();
    }

    private sealed class ModelState
    {
        public required PhysicalModelKey Key { get; init; }
        public string ProviderName { get; set; } = string.Empty;
        public long[] Buckets { get; } = new long[3600];
        public long LastSecond { get; private set; }
        public DateTimeOffset FirstObserved { get; init; }
        public DateTimeOffset? LastHealth { get; set; }
        public DateTimeOffset? OfflineSince { get; set; }
        public DateTimeOffset? LastFailure { get; set; }
        public bool Healthy { get; set; }
        public Guid NextReportId { get; set; } = Guid.NewGuid();

        public void Advance(long second)
        {
            if (second <= LastSecond) return;
            if (second - LastSecond >= 3600) Array.Clear(Buckets);
            else for (var at = LastSecond + 1; at <= second; at++) Buckets[(int)(at % 3600)] = 0;
            LastSecond = second;
        }

        public long Count(int minutes)
        {
            long count = 0;
            for (var offset = 0; offset < minutes * 60; offset++)
                count += Buckets[(int)((LastSecond - offset + 3600) % 3600)];
            return count;
        }
    }
}

public sealed record BackendSnapshot(PhysicalModelKey Key, string ProviderName,
    DateTimeOffset? LastHealth, DateTimeOffset? OfflineSince, DateTimeOffset? LastFailure, bool? Healthy,
    long FailuresInTenMinutes, BackendMonitoringRule? MatchingRule, Guid NextReportId, bool CanRecover);
