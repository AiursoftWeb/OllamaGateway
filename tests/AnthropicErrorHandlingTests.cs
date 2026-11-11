using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Controllers;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway;
using Aiursoft.OllamaGateway.Gateway.Chat;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Models.AnthropicViewModels;
using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public class AnthropicErrorHandlingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClientCancellation_DoesNotWriteErrorResponse(bool started)
    {
        var (controller, context, log, lifetime) = CreateController(started, true, new TaskCanceledException());

        await controller.Messages(new AnthropicMessageRequest());

        Assert.AreEqual(200, context.Response.StatusCode);
        Assert.AreEqual(0L, context.Response.Body.Length);
        Assert.IsFalse(log.Log.Success);
        Assert.AreEqual("Client canceled the request.", log.Log.Answer);
        Assert.IsFalse(lifetime.Aborted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartedResponseFailure_AbortsWithoutChangingStatusOrAppendingJson(bool timeout)
    {
        Exception error = timeout ? new TaskCanceledException("upstream failed") : new IOException("upstream failed");
        var (controller, context, log, lifetime) = CreateController(true, false, error);
        await context.Response.WriteAsync("event: message_start\n\n");
        var length = context.Response.Body.Length;

        await controller.Messages(new AnthropicMessageRequest());

        Assert.AreEqual(200, context.Response.StatusCode);
        Assert.AreEqual(length, context.Response.Body.Length);
        Assert.IsTrue(lifetime.Aborted);
        Assert.IsFalse(log.Log.Success);
        Assert.AreEqual("upstream failed", log.Log.Answer);
    }

    [TestMethod]
    public async Task UnstartedFailure_ReturnsJson500()
    {
        // 未由客户端取消的超时仍应作为服务器异常处理。
        var (controller, context, log, lifetime) = CreateController(false, false, new TaskCanceledException("upstream timeout"));

        await controller.Messages(new AnthropicMessageRequest());

        Assert.AreEqual(500, context.Response.StatusCode);
        Assert.AreEqual(500, log.Log.StatusCode);
        Assert.AreEqual("application/json", context.Response.ContentType);
        Assert.IsFalse(log.Log.Success);
        Assert.IsFalse(lifetime.Aborted);
        context.Response.Body.Position = 0;
        var json = await JsonNode.ParseAsync(context.Response.Body);
        Assert.AreEqual("error", json!["type"]!.GetValue<string>());
        Assert.AreEqual("internal_error", json["error"]!["type"]!.GetValue<string>());
    }

    private static (AnthropicController Controller, DefaultHttpContext Context, RequestLogContext Log, TestLifetimeFeature Lifetime)
        CreateController(bool started, bool canceled, Exception error)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new TestResponseFeature(started));
        var lifetime = new TestLifetimeFeature { RequestAborted = new CancellationToken(canceled) };
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();
        var log = new RequestLogContext();
        var tracker = new GatewayRequestTracker(log, null!, null!);
        var controller = new AnthropicController(log, NullLogger<AnthropicController>.Instance,
            tracker, null!, null!, new ThrowingCompiler(error), null!)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        return (controller, context, log, lifetime);
    }

    private sealed class TestResponseFeature(bool started) : HttpResponseFeature, IHttpResponseFeature
    {
        public override bool HasStarted => started;
        int IHttpResponseFeature.StatusCode
        {
            get => StatusCode;
            set
            {
                if (started)
                    throw new InvalidOperationException("StatusCode cannot be set because the response has already started.");
                StatusCode = value;
            }
        }
    }

    private sealed class TestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed class ThrowingCompiler(Exception error) : IChatRequestCompiler
    {
        public DecodedChatRequest Decode(ProtocolDialect clientDialect, string body) => throw error;
        public HttpRequestMessage CreateProviderRequest(DecodedChatRequest request, VirtualModel virtualModel,
            VirtualModelBackend backend) => throw new NotSupportedException();
    }
}
