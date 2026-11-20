using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

// Text on the Monitor feed is limited to the two display previews.
public sealed record TrafficEvent(Guid RequestId, int ModelId, int Attempt, int? BackendId, int? ProviderId,
    string Stage, long At = 0, long Sequence = 0)
{
    public string QuestionPreview { get; init; } = string.Empty;
    public string AnswerPreview { get; init; } = string.Empty;

    public bool Finished => Stage is "completed" or "failed" or "cancelled";
}

public sealed record TrafficBatch(long ServerTime, TrafficEvent[]? Baseline, TrafficEvent[] Events);

public sealed class TrafficJournal(TimeProvider clock) : ISingletonDependency
{
    private const int Capacity = 4096;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly Lock _gate = new();
    private readonly TrafficEvent?[] _events = new TrafficEvent[Capacity];
    // State immediately before the oldest retained event, including long-running requests.
    private readonly Dictionary<Guid, TrafficEvent> _baseline = new();
    private TaskCompletionSource? _changed;
    private long _sequence;

    public void Record(TrafficEvent item)
    {
        lock (_gate)
        {
            var index = (int)(_sequence % Capacity);
            if (_events[index] is { } oldest)
            {
                if (oldest.Finished)
                    _baseline.Remove(oldest.RequestId);
                else
                    _baseline[oldest.RequestId] = oldest;
            }
            _events[index] = item with { Sequence = ++_sequence, At = clock.GetUtcNow().ToUnixTimeMilliseconds() };
            _changed?.TrySetResult();
            _changed = null;
        }
    }

    public async IAsyncEnumerable<SseItem<TrafficBatch>> Read(string? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parts = cursor?.Split('/');
        long after = -1;
        if (parts is [var instance, var sequence] && instance == _instance && long.TryParse(sequence, out var parsed))
            after = parsed;

        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            TrafficBatch? batch = null;
            Task? wait = null;
            lock (_gate)
            {
                if (first || after != _sequence)
                {
                    var oldest = Math.Max(0, _sequence - Capacity);
                    var reset = after < oldest || after > _sequence;
                    var start = reset ? oldest : after;
                    var events = new TrafficEvent[_sequence - start];
                    for (var i = 0; i < events.Length; i++)
                        events[i] = _events[(start + i) % Capacity]!;
                    batch = new TrafficBatch(clock.GetUtcNow().ToUnixTimeMilliseconds(),
                        reset ? _baseline.Values.ToArray() : null, events);
                    after = _sequence;
                    first = false;
                }
                else
                {
                    _changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _changed.Task;
                }
            }
            if (batch != null)
                yield return new SseItem<TrafficBatch>(batch) { EventId = $"{_instance}/{after}" };
            else
                await wait!.WaitAsync(cancellationToken);
        }
    }
}
