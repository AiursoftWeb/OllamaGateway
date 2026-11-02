using System.Net.WebSockets;
using System.Text.Json;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class DashboardControllerTests : TestBase
{
    private async Task<ClientWebSocket> ConnectLiveAsync(string? origin = null)
    {
        var response = await PostForm("/Account/Login", new Dictionary<string, string>
        {
            ["EmailOrUserName"] = "admin@default.com",
            ["Password"] = "Admin@123456!"
        });
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Cookie", string.Join("; ",
            response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0])));
        socket.Options.SetRequestHeader("Origin", origin ?? Http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new UriBuilder(Http.BaseAddress!)
            {
                Scheme = "ws", Path = "/Dashboard/live-inference.ws"
            }.Uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<JsonElement> ReadLiveAsync(ClientWebSocket socket, Func<JsonElement, bool> matches)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                Assert.AreEqual(WebSocketMessageType.Text, frame.MessageType);
                body.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);
            using var document = JsonDocument.Parse(body.ToArray());
            if (matches(document.RootElement))
                return document.RootElement.Clone();
        }
    }

    [TestMethod]
    public async Task LiveInferenceSocket_StreamsArrivalCompletionAndReconnectSnapshot()
    {
        using var socket = await ConnectLiveAsync();
        await ReadLiveAsync(socket, state => state.GetProperty("items").GetArrayLength() == 0);
        var tracker = GetService<ActiveRequestTracker>();
        var request = tracker.BeginRequest("live-model", "first question", "display name");
        request.SetBackend(1, "physical-model");
        var busy = await ReadLiveAsync(socket, state => state.GetProperty("items").GetArrayLength() == 1 &&
            state.GetProperty("items")[0].GetProperty("backendModelName").GetString() == "physical-model");
        Assert.IsTrue(busy.GetProperty("items")[0].GetProperty("isActive").GetBoolean());
        Assert.AreEqual(string.Empty, busy.GetProperty("items")[0].GetProperty("answer").GetString());
        request.Complete(true, answer: "completed answer");
        await ReadLiveAsync(socket, state => state.GetProperty("items")[0].GetProperty("answer").GetString() == "completed answer");

        var next = tracker.BeginRequest("live-model", "next question");
        var nextBusy = await ReadLiveAsync(socket, state => state.GetProperty("items")[0].GetProperty("isActive").GetBoolean());
        Assert.AreEqual(string.Empty, nextBusy.GetProperty("items")[0].GetProperty("answer").GetString());
        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", closeTimeout.Token);
        next.Complete(true, answer: "answer while offline");
        using var reconnected = await ConnectLiveAsync();
        var restored = await ReadLiveAsync(reconnected, state => state.GetProperty("items").GetArrayLength() == 1);
        Assert.AreEqual("answer while offline", restored.GetProperty("items")[0].GetProperty("answer").GetString());
        await reconnected.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeTimeout.Token);
    }

    [TestMethod]
    public async Task LiveInferenceSocket_RejectsCrossOriginEvenForAdmin()
    {
        await Assert.ThrowsExactlyAsync<WebSocketException>(async () =>
        {
            using var socket = await ConnectLiveAsync("https://other.example");
        });
    }

    [TestMethod]
    public async Task LiveInferenceSocket_RequiresDashboardPermission()
    {
        var anonymous = await Http.GetAsync("/Dashboard/live-inference.ws");
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, anonymous.StatusCode);
        await RegisterAndLoginAsync();
        var denied = await Http.GetAsync("/Dashboard/live-inference.ws");
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("Code403", denied.Headers.Location!.OriginalString);
    }

    [TestMethod]
    public async Task GetIndex_NotAuthenticated_RedirectsToLogin()
    {
        var url = "/Dashboard/Index";

        var response = await Http.GetAsync(url);

        // Assert
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }

    [TestMethod]
    public async Task GetIndex_AuthenticatedWithoutPermission_RedirectsToAccessDenied()
    {
        await RegisterAndLoginAsync();
        var url = "/Dashboard/Index";

        var response = await Http.GetAsync(url);

        // Assert — a regular user without CanViewSystemContext should be denied.
        Assert.AreEqual(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.IsTrue(location.Contains("Code403"), $"Expected redirect to access denied, got: {location}");
    }

    [TestMethod]
    public async Task GetIndex_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/Dashboard/Index";

        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Admin Center"));
    }

    [TestMethod]
    public async Task GetMonitor_Authenticated_ReturnsSuccess()
    {
        await LoginAsAdmin();
        var url = "/Dashboard/Monitor";

        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Traffic Visualization"));
        Assert.IsTrue(content.Contains("mermaid"));
    }

    [TestMethod]
    public async Task GetGuide_DescribesCurrentSemanticIrArchitecture()
    {
        await LoginAsAdmin();

        var response = await Http.GetAsync("/Dashboard/Guide");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("POST /v1/messages", html);
        Assert.Contains("POST /v1/responses", html);
        Assert.Contains("data-guide-architecture=\"semantic-ir-v2\"", html);
        Assert.Contains("data-stage=\"request-ir\"", html);
        Assert.Contains("data-stage=\"per-attempt-adapter\"", html);
        Assert.Contains("data-stage=\"event-ir\"", html);
        Assert.Contains("data-route-mode=\"same-dialect\"", html);
        Assert.Contains("data-route-mode=\"cross-dialect\"", html);
        Assert.Contains("data-responses-contract=\"native-state\"", html);
        Assert.Contains("data-guide-status=\"default-model-missing\"", html);
        Assert.Contains("data-retry-semantics=\"attempt-budget\"", html);
        Assert.Contains("data-timeout-scope=\"headers-only\"", html);
        Assert.Contains("data-dashboard-timeouts=\"ollama-ps-3-version-5-openai-models-10\"", html);
        Assert.DoesNotContain("Any OpenAI-compatible or Ollama-native client sends a request.", html);
        Assert.DoesNotContain("feeds the Dashboard charts in real-time", html);
        Assert.DoesNotContain("Every request is logged asynchronously", html);
    }

    [TestMethod]
    public async Task GetIndex_WithProviderAndModels_ShowsCorrectModelCount()
    {
        await LoginAsAdmin();

        var db = GetService<TemplateDbContext>();
        var provider = new OllamaProvider
        {
            Name = "Stats Provider",
            BaseUrl = "http://localhost:11434",
            ProviderType = ProviderType.Ollama
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();

        var vm1 = new VirtualModel { Name = "vm1:latest", Type = ModelType.Chat };
        var vm2 = new VirtualModel { Name = "vm2:latest", Type = ModelType.Chat };
        db.VirtualModels.AddRange(vm1, vm2);
        await db.SaveChangesAsync();

        db.VirtualModelBackends.Add(new VirtualModelBackend
        {
            VirtualModelId = vm1.Id,
            ProviderId = provider.Id,
            UnderlyingModelName = "p1",
            Enabled = true
        });
        db.VirtualModelBackends.Add(new VirtualModelBackend
        {
            VirtualModelId = vm2.Id,
            ProviderId = provider.Id,
            UnderlyingModelName = "p2",
            Enabled = true
        });
        await db.SaveChangesAsync();

        var url = "/Dashboard/Index";
        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("Stats Provider"));
        Assert.IsTrue(content.Contains("2</td>"), "Should show model count of 2 for Stats Provider");
    }

    [TestMethod]
    public async Task GetMonitor_WithBusyModel_HighlightsModel()
    {
        await LoginAsAdmin();

        // Add a virtual model and backend to the DB
        var db = GetService<TemplateDbContext>();
        var provider = new OllamaProvider
        {
            Name = "Test Provider",
            BaseUrl = "http://localhost:11434",
            ProviderType = ProviderType.Ollama
        };
        db.OllamaProviders.Add(provider);
        await db.SaveChangesAsync();

        var vm = new VirtualModel
        {
            Name = "test-virtual-model",
            Type = ModelType.Chat
        };
        db.VirtualModels.Add(vm);
        await db.SaveChangesAsync();

        var backend = new VirtualModelBackend
        {
            VirtualModelId = vm.Id,
            ProviderId = provider.Id,
            UnderlyingModelName = "test-physical-model",
            Enabled = true
        };
        db.VirtualModelBackends.Add(backend);
        await db.SaveChangesAsync();

        // Mark a model as busy
        var tracker = GetService<ActiveRequestTracker>();
        tracker.StartRequest(vm.Name, "test question", provider.Id, "test-physical-model");

        var url = "/Dashboard/Monitor";
        var response = await Http.GetAsync(url);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(content.Contains("virtualSvcBusy"), "Mermaid code should contain virtualSvcBusy class when a model is busy.");
        Assert.IsTrue(content.Contains("physicalBusy"), "Mermaid code should contain physicalBusy class when a physical model is busy.");
    }
}
