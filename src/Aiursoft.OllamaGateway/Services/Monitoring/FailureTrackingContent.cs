using System.Net;
using System.Text;
using System.Text.Json;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Services.Monitoring;

// Observe the upstream body as it is consumed; never buffer a streaming model response.
public sealed class FailureTrackingContent(HttpContent inner, Action failure, CancellationToken clientCancellation, BackendProtocol? streamingProtocol = null) : HttpContent
{
    private int _reported;

    public static HttpContent Wrap(HttpContent content, Action failure, CancellationToken clientCancellation, BackendProtocol? streamingProtocol = null)
    {
        var wrapper = new FailureTrackingContent(content, failure, clientCancellation, streamingProtocol);
        foreach (var header in content.Headers) wrapper.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return wrapper;
    }

    private void Report(Exception exception)
    {
        if (!clientCancellation.IsCancellationRequested &&
            exception is IOException or HttpRequestException or OperationCanceledException &&
            Interlocked.Exchange(ref _reported, 1) == 0) failure();
    }

    protected override async Task<Stream> CreateContentReadStreamAsync() =>
        await CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        try { return new ObservedStream(await inner.ReadAsStreamAsync(cancellationToken), Report, streamingProtocol); }
        catch (Exception ex) { Report(ex); throw; }
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await using var observed = await CreateContentReadStreamAsync();
        await observed.CopyToAsync(stream);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = inner.Headers.ContentLength ?? 0;
        return inner.Headers.ContentLength.HasValue;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    private sealed class ObservedStream(Stream innerStream, Action<Exception> report, BackendProtocol? protocol) : Stream
    {
        private readonly MemoryStream _line = new();
        private bool _lineTooLong;
        private bool _completed;

        private void Observe(ReadOnlySpan<byte> bytes, bool end)
        {
            foreach (var value in bytes)
            {
                if (value == (byte)'\n') { ObserveLine(); continue; }
                if (_line.Length < 65536) _line.WriteByte(value);
                else _lineTooLong = true;
            }
            if (!end) return;
            ObserveLine();
            if (protocol.HasValue && !_completed) report(new IOException("The upstream model stream ended without a completion record."));
        }

        private void ObserveLine()
        {
            if (!_lineTooLong && _line.Length > 0)
            {
                var line = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).Trim();
                if (line.StartsWith("data:", StringComparison.Ordinal)) line = line[5..].Trim();
                if (line == "[DONE]") _completed = true;
                else
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        var root = json.RootElement;
                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                                report(new IOException("The upstream model returned an error record."));
                            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                            {
                                if (type.GetString() == "response.completed") _completed = true;
                                if (type.GetString() is "response.failed" or "response.incomplete" or "error")
                                    report(new IOException("The upstream response failed or was incomplete."));
                            }
                            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True) _completed = true;
                            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
                                choices.EnumerateArray().Any(choice => choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String))
                                _completed = true;
                        }
                    }
                    catch (JsonException) { /* Preserve unrecognized wire data without logging its contents. */ }
                }
            }
            _line.SetLength(0);
            _lineTooLong = false;
        }

        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => innerStream.Length;
        public override long Position { get => innerStream.Position; set => innerStream.Position = value; }
        public override void Flush() => innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            try { var read = innerStream.Read(buffer, offset, count); if (count > 0) Observe(buffer.AsSpan(offset, read), read == 0); return read; }
            catch (Exception ex) { report(ex); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { var read = await innerStream.ReadAsync(buffer, cancellationToken); if (buffer.Length > 0) Observe(buffer.Span[..read], read == 0); return read; }
            catch (Exception ex) { report(ex); throw; }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { innerStream.Dispose(); _line.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
