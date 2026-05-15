using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class ProviderConcurrencyLimiterTests
{
    /// <summary>
    /// BUG REPRODUCTION: Without concurrency limiting, 5 concurrent requests
    /// all hit the same backend simultaneously. If the backend is slow, all 5
    /// timeout → 5 ReportFailure calls → circuit breaker bans the backend after
    /// just 3 failures, even though the backend is perfectly healthy (just overloaded).
    /// </summary>
    [TestMethod]
    public async Task WithoutLimiter_ConcurrentTimeouts_TriggerSpuriousBan()
    {
        var selector = new ModelSelector();
        var vm = new VirtualModel
        {
            Name = "test-model",
            VirtualModelBackends = new List<VirtualModelBackend>
            {
                new() { Id = 1, Enabled = true, IsHealthy = true, UnderlyingModelName = "m1" }
            }
        };

        // Simulate 5 concurrent requests that all timeout against a busy backend
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            selector.ReportFailure(1); // each timeout calls ReportFailure
        }));
        await Task.WhenAll(tasks);

        // After 3+ failures, the healthy backend is BANNED.
        // This is the bug: the backend isn't broken, just overloaded by concurrency.
        Assert.IsNull(selector.SelectBackend(vm),
            "Backend was banned even though it was healthy — just overwhelmed by concurrent requests");
        Assert.IsNotNull(selector.GetBanUntil(1));
    }

    /// <summary>
    /// FIX VERIFICATION: With MaxParallelism=1, only one request reaches the
    /// backend at a time. Others queue and wait (without timing out). The backend
    /// handles each request successfully. No spurious bans.
    /// </summary>
    [TestMethod]
    public async Task WithMaxParallelismOne_RequestsQueue_NoSpuriousBan()
    {
        var limiter = new ProviderConcurrencyLimiter();
        var selector = new ModelSelector();

        var concurrentCount = 0;
        var maxConcurrentObserved = 0;
        var completedCount = 0;

        async Task SimulateRequest(int requestId, CancellationToken ct)
        {
            // Queue for a slot — waiting here does NOT count as request timeout.
            // Only 1 request enters the critical section at a time.
            await using var slot = await limiter.AcquireAsync(
                providerId: 1,
                maxParallelism: 1,
                cancellationToken: ct);

            var current = Interlocked.Increment(ref concurrentCount);
            maxConcurrentObserved = Math.Max(maxConcurrentObserved, current);

            // Simulate backend processing (e.g., 50ms)
            await Task.Delay(50, ct);

            Interlocked.Decrement(ref concurrentCount);
            Interlocked.Increment(ref completedCount);

            // Backend responded successfully — no timeout, no ban
            selector.ReportSuccess(1);
        }

        // 10 concurrent requests against MaxParallelism=1
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tasks = Enumerable.Range(0, 10)
            .Select(i => SimulateRequest(i, cts.Token));
        await Task.WhenAll(tasks);

        // Only 1 request ever ran concurrently — the limiter serialized them
        Assert.AreEqual(1, maxConcurrentObserved,
            "At most 1 request should hit the backend at a time");
        Assert.AreEqual(10, completedCount,
            "All 10 requests should complete successfully");

        // Backend is NOT banned — ReportSuccess cleared any state
        var vm = new VirtualModel
        {
            Name = "test-model",
            VirtualModelBackends = new List<VirtualModelBackend>
            {
                new() { Id = 1, Enabled = true, IsHealthy = true, UnderlyingModelName = "m1" }
            }
        };
        Assert.IsNotNull(selector.SelectBackend(vm),
            "Backend should still be available — it was just busy, not broken");
        Assert.IsNull(selector.GetBanUntil(1));
    }

    [TestMethod]
    public async Task MaxParallelism_Zero_AllowsUnlimitedConcurrency()
    {
        var limiter = new ProviderConcurrencyLimiter();

        var concurrentCount = 0;
        var maxConcurrentObserved = 0;

        async Task RunWithLimiter()
        {
            await using var slot = await limiter.AcquireAsync(1, maxParallelism: 0, CancellationToken.None);
            var current = Interlocked.Increment(ref concurrentCount);
            maxConcurrentObserved = Math.Max(maxConcurrentObserved, current);
            await Task.Delay(50);
            Interlocked.Decrement(ref concurrentCount);
        }

        // 10 requests with MaxParallelism=0 — all proceed immediately
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => RunWithLimiter()));

        // 0 means "no limit", so all 10 run concurrently
        Assert.AreEqual(10, maxConcurrentObserved,
            "MaxParallelism=0 should allow unlimited concurrency");
    }

    [TestMethod]
    public async Task MaxParallelism_Two_AllowsTwoConcurrent()
    {
        var limiter = new ProviderConcurrencyLimiter();

        var concurrentCount = 0;
        var maxConcurrentObserved = 0;
        var tcs = new TaskCompletionSource();

        async Task RunWithLimiter()
        {
            await using var slot = await limiter.AcquireAsync(1, maxParallelism: 2, CancellationToken.None);
            var current = Interlocked.Increment(ref concurrentCount);
            maxConcurrentObserved = Math.Max(maxConcurrentObserved, current);
            // Hold the slot until signaled
            await tcs.Task;
            Interlocked.Decrement(ref concurrentCount);
        }

        // Start 5 tasks with MaxParallelism=2
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() => RunWithLimiter())).ToList();

        // Give them time to acquire
        await Task.Delay(200);

        Assert.AreEqual(2, maxConcurrentObserved,
            "At most 2 requests should run concurrently");

        // Release all
        tcs.SetResult();
        await Task.WhenAll(tasks);
    }

    [TestMethod]
    public async Task QueueWait_RespectsCancellationToken()
    {
        var limiter = new ProviderConcurrencyLimiter();

        // Hold the only slot indefinitely
        var holdTcs = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            await using var slot = await limiter.AcquireAsync(1, maxParallelism: 1, CancellationToken.None);
            await holdTcs.Task;
        });

        // Give the holder time to acquire
        await Task.Delay(100);

        // Second request tries to acquire with a short cancellation
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var canceled = false;
        try
        {
            await using var slot = await limiter.AcquireAsync(1, maxParallelism: 1, cts.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        Assert.IsTrue(canceled, "Second request should be canceled when waiting in queue too long");

        holdTcs.SetResult();
    }
}
