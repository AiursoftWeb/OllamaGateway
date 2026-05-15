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

        async Task SimulateRequest(CancellationToken ct)
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
        var token = cts.Token;
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => SimulateRequest(token));
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

    /// <summary>
    /// PRODUCTION BUG REPRODUCTION: When BackendInvoker.SendAsync() acquires a semaphore
    /// slot (line 36-37) and the CLIENT disconnects during the HTTP call to the backend,
    /// the exception filter on line 83 intentionally skips the catch block:
    ///
    ///   catch (Exception ex) when (
    ///       ex is not OperationCanceledException ||
    ///       !clientCancellation.IsCancellationRequested)
    ///
    /// When clientCancellation IS signaled, the filter returns false → exception propagates
    /// WITHOUT releasing the slot (line 85 is skipped, lines 100-101 are also skipped).
    /// The semaphore is permanently exhausted → all future requests queue forever.
    ///
    /// This test simulates the leak: acquire a slot and abandon it without disposal,
    /// then prove that subsequent acquire attempts are permanently blocked.
    /// </summary>
    [TestMethod]
    public async Task LeakedSemaphore_PermanentlyBlocksSubsequentRequests()
    {
        var limiter = new ProviderConcurrencyLimiter();

        // Simulate BackendInvoker line 36-37: acquire the only slot (1/1 → 0/1)
        var leakedSlot = await limiter.AcquireAsync(
            providerId: 1, maxParallelism: 1, CancellationToken.None);

        // BUG: BackendInvoker does NOT dispose concurrencySlot when
        // clientCancellation fires during client.SendAsync(). The exception
        // propagates past both the catch block (line 83 filter) and the
        // post-loop cleanup (lines 100-101). The slot is abandoned.

        // VERIFY: the semaphore is now at 0/1. Any subsequent AcquireAsync
        // will block until its cancellation token fires — the slot is gone forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var secondRequestSucceeded = false;
        try
        {
            await limiter.AcquireAsync(providerId: 1, maxParallelism: 1, cts.Token);
            secondRequestSucceeded = true;
        }
        catch (OperationCanceledException)
        {
            // Expected: the leaked slot leaves 0 available, so WaitAsync blocks
            // until the cancellation token fires after 300ms.
        }

        Assert.IsFalse(secondRequestSucceeded,
            "BUG CONFIRMED: a leaked semaphore slot permanently blocks all " +
            "subsequent requests. When BackendInvoker fails to release the slot " +
            "after client cancellation, MaxParallelism=1 becomes MaxParallelism=0 " +
            "forever. This is why Ollama is idle but requests queue infinitely.");

        // Cleanup so this test doesn't leak into others
        await leakedSlot.DisposeAsync();
    }

    /// <summary>
    /// CONTROL: When the slot IS properly disposed (the correct behavior),
    /// subsequent requests can acquire it immediately.
    /// </summary>
    [TestMethod]
    public async Task ProperlyDisposedSlot_AllowsSubsequentRequests()
    {
        var limiter = new ProviderConcurrencyLimiter();

        await using (await limiter.AcquireAsync(1, maxParallelism: 1, CancellationToken.None))
        {
            // Slot acquired and immediately released via await using disposal
        }

        // Next request should succeed without blocking
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await using var nextSlot = await limiter.AcquireAsync(1, maxParallelism: 1, cts.Token);

        // If we get here, the slot was successfully acquired → the previous
        // disposal correctly released the semaphore.
    }
}
