using System.Threading.Channels;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services.Clickhouse;

/// <summary>
/// Lock-free in-memory buffer that decouples the request hot-path from ClickHouse writes.
/// The middleware enqueues; the background job dequeues and flushes in batches.
/// </summary>
public class RequestLogBuffer : ISingletonDependency
{
    private readonly Channel<RequestLog> _channel = Channel.CreateBounded<RequestLog>(
        new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public void Enqueue(RequestLog log) => _channel.Writer.TryWrite(log);

    public int Drain(List<RequestLog> batch)
    {
        var reader = _channel.Reader;
        while (reader.TryRead(out var log))
            batch.Add(log);
        return batch.Count;
    }
}
