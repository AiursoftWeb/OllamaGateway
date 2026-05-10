using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.InMemory;
using Aiursoft.OllamaGateway.Services.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.Protected;
using System.Net;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class ModelWarmupServiceTests
{
    private InMemoryContext _dbContext = null!;
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private Mock<ILogger<ModelWarmupService>> _loggerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new InMemoryContext(options);
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<ModelWarmupService>>();
    }

    [TestMethod]
    public async Task TestExecuteAsync_NoProviders()
    {
        var service = new ModelWarmupService(_dbContext, _httpClientFactoryMock.Object, _loggerMock.Object);
        await service.ExecuteAsync();
        
        _httpClientFactoryMock.Verify(x => x.CreateClient(It.IsAny<string>()), Times.Never);
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

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{}")
            });

        var client = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(client);

        var service = new ModelWarmupService(_dbContext, _httpClientFactoryMock.Object, _loggerMock.Object);
        await service.ExecuteAsync();

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => 
                req.Method == HttpMethod.Post && 
                req.RequestUri!.ToString().Contains("/api/chat")),
            ItExpr.IsAny<CancellationToken>()
        );
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

        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{}")
            });

        var client = new HttpClient(handlerMock.Object);
        _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(client);

        var service = new ModelWarmupService(_dbContext, _httpClientFactoryMock.Object, _loggerMock.Object);
        await service.ExecuteAsync();

        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => 
                req.Method == HttpMethod.Post && 
                req.RequestUri!.ToString().Contains("/api/embeddings")),
            ItExpr.IsAny<CancellationToken>()
        );
    }
}
