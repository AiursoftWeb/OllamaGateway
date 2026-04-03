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
    public DateTime LastStartedAt = DateTime.UtcNow;
}

/// <summary>
/// Thread-safe singleton that tracks which virtual models are currently handling
/// inference requests. Used by the Dashboard to visualise live gateway load.
/// </summary>
public class ActiveRequestTracker : ISingletonDependency
{
    private readonly ConcurrentDictionary<string, ActiveModelRequestInfo> _state = new();

    /// <summary>
    /// Call immediately before forwarding a request to the upstream model.
    /// </summary>
    public void StartRequest(string modelName, string question)
    {
        var info = _state.GetOrAdd(modelName, _ => new ActiveModelRequestInfo());
        lock (info)
        {
            info.ActiveCount++;
            info.LastQuestion = question.Length > 30 ? question[..30] : question;
            info.LastStartedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Call in a finally block once the upstream response has been fully streamed.
    /// </summary>
    public void EndRequest(string modelName)
    {
        if (!_state.TryGetValue(modelName, out var info)) return;
        lock (info)
        {
            info.ActiveCount = Math.Max(0, info.ActiveCount - 1);
            if (info.ActiveCount == 0)
                info.LastQuestion = string.Empty;
        }
    }

    public IReadOnlyDictionary<string, ActiveModelRequestInfo> GetAll() => _state;
}
