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
    public DateTime CompletedAt { get; init; }
    public double DurationMs { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
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
    /// Call immediately before forwarding a request to the upstream model.
    /// </summary>
    public void StartRequest(string modelName, string question, int providerId, string backendModelName, string apiKeyName = "")
    {
        var info = _state.GetOrAdd(modelName, _ => new ActiveModelRequestInfo());
        lock (info)
        {
            info.ActiveCount++;
            info.LastQuestion = question.Length > 30 ? question[..30] : question;
            info.BackendModelName = backendModelName;
            info.ApiKeyName = apiKeyName;
            info.LastStartedAt = DateTime.UtcNow;
        }

        _physicalState.AddOrUpdate((providerId, backendModelName), 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Call in a finally block once the upstream response has been fully streamed.
    /// </summary>
    public void EndRequest(string modelName, int providerId, string backendModelName, bool success, string errorMessage = "")
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
            CompletedAt = now,
            DurationMs = duration,
            ErrorMessage = errorMessage
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
