using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class RateLimitServiceTests
{
    [TestMethod]
    public async Task HangMode_StopsWaitingWhenClientDisconnects()
    {
        var service = new RateLimitService();
        var apiKey = LimitedKey(hang: true);

        Assert.IsTrue(await service.IsAllowedAsync(apiKey));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => service.IsAllowedAsync(apiKey, cts.Token));
    }

    [TestMethod]
    public async Task RejectMode_ReturnsFalseImmediatelyWhenWindowIsFull()
    {
        var service = new RateLimitService();
        var apiKey = LimitedKey(hang: false);

        Assert.IsTrue(await service.IsAllowedAsync(apiKey));
        Assert.IsFalse(await service.IsAllowedAsync(apiKey));
    }

    private static ApiKey LimitedKey(bool hang) => new()
    {
        Id = 7,
        Name = "limited",
        Key = "secret",
        UserId = "user",
        RateLimitEnabled = true,
        RateLimitHang = hang,
        MaxRequests = 1,
        TimeWindowSeconds = 60
    };
}
