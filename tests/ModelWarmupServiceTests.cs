using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.InMemory;
using Aiursoft.OllamaGateway.Services.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class ModelWarmupServiceTests
{
    private InMemoryContext _dbContext = null!;
    private RecordingClientFactory _httpClientFactory = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new InMemoryContext(options);
        _httpClientFactory = new RecordingClientFactory();
    }

    [TestMethod]
    public async Task TestExecuteAsync_NoProviders()
    {
        var service = new ModelWarmupService(_dbContext, _httpClientFactory, NullLogger<ModelWarmupService>.Instance);
        await service.ExecuteAsync();

        Assert.AreEqual(0, _httpClientFactory.CreateClientCalls);
    }

    [TestMethod]
    public async Task TestExecuteAsync_WithWarmupModels()
    {
        var provider = new OllamaProvider
        {
            Id = 1,
            Name = "Test Provider",
            BaseUrl = "http://localhost:11434",
            ProviderType = ProviderType.Ollama,
            KeepAlive = "5m",
            WarmupModelsJson = "[{\"Name\": \"llama2\", \"IsEmbedding\": false}]"
        };
        _dbContext.OllamaProviders.Add(provider);
        await _dbContext.SaveChangesAsync();

        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        _httpClientFactory.Client = client;

        var service = new ModelWarmupService(_dbContext, _httpClientFactory, NullLogger<ModelWarmupService>.Instance);
        await service.ExecuteAsync();

        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        StringAssert.Contains(handler.Requests[0].Uri, "/api/chat");
    }

    [TestMethod]
    public async Task TestExecuteAsync_WithEmbeddingWarmupModels()
    {
        var provider = new OllamaProvider
        {
            Id = 1,
            Name = "Test Provider",
            BaseUrl = "http://localhost:11434",
            ProviderType = ProviderType.Ollama,
            KeepAlive = "5m",
            WarmupModelsJson = "[{\"Name\": \"bge-m3\", \"IsEmbedding\": true}]"
        };
        _dbContext.OllamaProviders.Add(provider);
        await _dbContext.SaveChangesAsync();

        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        _httpClientFactory.Client = client;

        var service = new ModelWarmupService(_dbContext, _httpClientFactory, NullLogger<ModelWarmupService>.Instance);
        await service.ExecuteAsync();

        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        StringAssert.Contains(handler.Requests[0].Uri, "/api/embeddings");
    }
    [TestCleanup]
    public void Cleanup() => _dbContext.Dispose();

    private sealed class RecordingClientFactory : IHttpClientFactory
    {
        public HttpClient? Client { get; set; }
        public int CreateClientCalls { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateClientCalls++;
            return Client ?? throw new InvalidOperationException("No HTTP client was configured.");
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
