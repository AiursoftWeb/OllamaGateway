using System.Collections.Concurrent;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

/// <summary>
/// In-memory state of active inference requests for one virtual model.
/// All mutations are guarded by a lock on the instance itself.
/// </summary>
public class ActiveModelRequestInfo
{
    public int ActiveCount;
    public string LastQuestion = string.Empty;
    public string LastFullQuestion = string.Empty;
    public string BackendModelName = string.Empty;
    public string ApiKeyName = string.Empty;
    public DateTime LastStartedAt = DateTime.UtcNow;
    public DateTime? LastCompletedAt;
}

/// <summary>
/// A single completed request entry stored in the ring buffer.
/// </summary>
public class RecentRequestEntry
{
    public string Status { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string BackendModelName { get; init; } = string.Empty;
    public string ApiKeyName { get; init; } = string.Empty;
    public string Question { get; init; } = string.Empty;
    public string FullQuestion { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; }
    public double DurationMs { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
}

/// <summary>
/// Tracks one request independently so its physical backend can change safely while
/// the virtual request remains active for its entire lifetime.
/// </summary>
public sealed class ActiveRequestRegistration
{
    private readonly ActiveRequestTracker _tracker;
    private readonly object _gate = new();
    private (int ProviderId, string ModelName)? _backend;
    private bool _completed;

    internal string ModelName { get; }
    internal string Question { get; }
    internal string ShortQuestion { get; }
    internal string ApiKeyName { get; }
    internal DateTime StartedAt { get; } = DateTime.UtcNow;

    internal ActiveRequestRegistration(
        ActiveRequestTracker tracker,
        string modelName,
        string question,
        string apiKeyName)
    {
        _tracker = tracker;
        ModelName = modelName;
        Question = question;
        ShortQuestion = question.Length > 30 ? question[..30] : question;
        ApiKeyName = apiKeyName;
    }

    public void SetBackend(int providerId, string backendModelName)
    {
        lock (_gate)
        {
            if (_completed)
                return;

            var next = (providerId, backendModelName);
            if (_backend == next)
                return;

            _tracker.SwitchPhysicalBackend(ModelName, _backend, next);
            _backend = next;
        }
    }

    public void ClearBackend()
    {
        lock (_gate)
        {
            if (_completed || _backend == null)
                return;

            _tracker.SwitchPhysicalBackend(ModelName, _backend, null);
            _backend = null;
        }
    }

    public void Complete(bool success, string errorMessage = "", string answer = "")
    {
        lock (_gate)
        {
            if (_completed)
                return;

            _completed = true;
            var backend = _backend;
            _backend = null;
            _tracker.CompleteRequest(this, backend, success, errorMessage, answer);
        }
    }
}

/// <summary>
/// Thread-safe singleton that tracks which virtual models are currently handling
/// inference requests and keeps a ring buffer of the last 50 completed requests.
/// </summary>
public class ActiveRequestTracker : ISingletonDependency
{
    private readonly ConcurrentDictionary<string, ActiveModelRequestInfo> _state = new();
    private readonly ConcurrentDictionary<(int providerId, string modelName), int> _physicalState = new();

    // Ring buffer for the last 50 completed requests
    private readonly RecentRequestEntry[] _recentBuffer = new RecentRequestEntry[50];
    private int _recentIndex = -1;
    private int _recentCount;
    private readonly object _recentLock = new();

    /// <summary>
    /// Starts one virtual request without guessing which physical backend will be used.
    /// The returned registration can be moved between physical backends as attempts change.
    /// </summary>
    public ActiveRequestRegistration BeginRequest(string modelName, string question, string apiKeyName = "")
    {
        var registration = new ActiveRequestRegistration(this, modelName, question, apiKeyName);
        var info = _state.GetOrAdd(modelName, _ => new ActiveModelRequestInfo());
        lock (info)
        {
            info.ActiveCount++;
            info.LastQuestion = registration.ShortQuestion;
            info.LastFullQuestion = question;
            info.ApiKeyName = apiKeyName;
            info.LastStartedAt = registration.StartedAt;
        }

        return registration;
    }

    /// <summary>
    /// Call immediately before forwarding a request to the upstream model.
    /// </summary>
    public void StartRequest(string modelName, string question, int providerId, string backendModelName, string apiKeyName = "")
    {
        var info = _state.GetOrAdd(modelName, _ => new ActiveModelRequestInfo());
        lock (info)
        {
            info.ActiveCount++;
            info.LastQuestion = question.Length > 30 ? question[..30] : question;
            info.LastFullQuestion = question;
            info.BackendModelName = backendModelName;
            info.ApiKeyName = apiKeyName;
            info.LastStartedAt = DateTime.UtcNow;
        }

        _physicalState.AddOrUpdate((providerId, backendModelName), 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Call in a finally block once the upstream response has been fully streamed.
    /// </summary>
    public void EndRequest(string modelName, int providerId, string backendModelName, bool success, string errorMessage = "", string answer = "")
    {
        if (_state.TryGetValue(modelName, out var info))
        {
            lock (info)
            {
                info.ActiveCount = Math.Max(0, info.ActiveCount - 1);
                if (info.ActiveCount == 0)
                    info.LastCompletedAt = DateTime.UtcNow;
            }
        }

        _physicalState.AddOrUpdate((providerId, backendModelName), 0, (_, count) => Math.Max(0, count - 1));

        // Write to ring buffer
        var now = DateTime.UtcNow;
        var startedAt = info?.LastStartedAt ?? now;
        var duration = (now - startedAt).TotalMilliseconds;
        if (duration < 0) duration = 0;

        var entry = new RecentRequestEntry
        {
            Status = success ? "Completed" : "Failed",
            ModelName = modelName,
            BackendModelName = backendModelName,
            ApiKeyName = info?.ApiKeyName ?? string.Empty,
            Question = info?.LastQuestion ?? string.Empty,
            FullQuestion = info?.LastFullQuestion ?? string.Empty,
            CompletedAt = now,
            DurationMs = duration,
            ErrorMessage = errorMessage,
            Answer = answer
        };

        lock (_recentLock)
        {
            _recentIndex = (_recentIndex + 1) % 50;
            _recentBuffer[_recentIndex] = entry;
            if (_recentCount < 50) _recentCount++;
        }
    }

    internal void SwitchPhysicalBackend(
        string virtualModelName,
        (int ProviderId, string ModelName)? previous,
        (int ProviderId, string ModelName)? next)
    {
        if (previous.HasValue)
        {
            _physicalState.AddOrUpdate(
                (previous.Value.ProviderId, previous.Value.ModelName),
                0,
                (_, count) => Math.Max(0, count - 1));
        }

        if (next.HasValue)
        {
            _physicalState.AddOrUpdate(
                (next.Value.ProviderId, next.Value.ModelName),
                1,
                (_, count) => count + 1);

            if (_state.TryGetValue(virtualModelName, out var info))
            {
                lock (info)
                {
                    info.BackendModelName = next.Value.ModelName;
                }
            }
        }
    }

    internal void CompleteRequest(
        ActiveRequestRegistration registration,
        (int ProviderId, string ModelName)? backend,
        bool success,
        string errorMessage,
        string answer)
    {
        if (backend.HasValue)
        {
            _physicalState.AddOrUpdate(
                (backend.Value.ProviderId, backend.Value.ModelName),
                0,
                (_, count) => Math.Max(0, count - 1));
        }

        if (_state.TryGetValue(registration.ModelName, out var info))
        {
            lock (info)
            {
                info.ActiveCount = Math.Max(0, info.ActiveCount - 1);
                if (info.ActiveCount == 0)
                    info.LastCompletedAt = DateTime.UtcNow;
            }
        }

        var now = DateTime.UtcNow;
        var entry = new RecentRequestEntry
        {
            Status = success ? "Completed" : "Failed",
            ModelName = registration.ModelName,
            BackendModelName = backend?.ModelName ?? string.Empty,
            ApiKeyName = registration.ApiKeyName,
            Question = registration.ShortQuestion,
            FullQuestion = registration.Question,
            CompletedAt = now,
            DurationMs = Math.Max(0, (now - registration.StartedAt).TotalMilliseconds),
            ErrorMessage = errorMessage,
            Answer = answer
        };

        lock (_recentLock)
        {
            _recentIndex = (_recentIndex + 1) % 50;
            _recentBuffer[_recentIndex] = entry;
            if (_recentCount < 50) _recentCount++;
        }
    }

    public IReadOnlyDictionary<string, ActiveModelRequestInfo> GetAll() => _state;

    public HashSet<(int providerId, string modelName)> GetBusyPhysicalModels()
    {
        return _physicalState
            .Where(kv => kv.Value > 0)
            .Select(kv => kv.Key)
            .ToHashSet();
    }

    /// <summary>
    /// Returns the last 50 completed requests, most recent first.
    /// </summary>
    public List<RecentRequestEntry> GetRecentRequests()
    {
        lock (_recentLock)
        {
            var result = new List<RecentRequestEntry>(_recentCount);
            if (_recentCount == 0) return result;

            for (int i = 0; i < _recentCount; i++)
            {
                var idx = (_recentIndex - i + 50) % 50;
                result.Add(_recentBuffer[idx]);
            }
            return result;
        }
    }

    /// <summary>
    /// Extracts the first line of an error answer for dashboard display.
    /// </summary>
    public static string GetErrorSummary(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return string.Empty;
        var firstLine = answer.Split('\n')[0].Trim();
        return firstLine.Length > 200 ? firstLine[..200] : firstLine;
    }
}
