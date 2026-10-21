using System.Net;
using Aiursoft.OllamaGateway.Gateway.Execution;
using System.Text;
using System.Text.Json;
using Aiursoft.AlertCenter.SDK.Models;
using Aiursoft.OllamaGateway.Configuration;
using Aiursoft.OllamaGateway.Authorization;
using System.Text.RegularExpressions;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.BackgroundJobs;
using Aiursoft.OllamaGateway.Services.Monitoring;
using Aiursoft.OllamaGateway.Tests.IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.OllamaGateway.Tests;

[TestClass]
public sealed class BackendMonitoringTests
{
    internal sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan elapsed) => Now += elapsed;
    }

    internal static VirtualModelBackend Backend(int providerId = 1, string model = "model") => new()
    {
        ProviderId = providerId, UnderlyingModelName = model, Enabled = true, IsHealthy = true,
        Provider = new OllamaProvider { Id = providerId, Name = "Test provider", BaseUrl = "https://provider.test" }
    };

    [TestMethod]
    [DataRow(5, null)]
    [DataRow(6, "sev3")]
    [DataRow(20, "sev3")]
    [DataRow(21, "sev2")]
    public void RollingFailures_UseStrictThresholdsAndHighestSeverity(int failures, string? expected)
    {
        var monitor = new BackendMonitor(new ManualClock());
        for (var i = 0; i < failures; i++) monitor.Failure(Backend());
        Assert.AreEqual(expected, monitor.Snapshot(BackendMonitoringRule.Parse(BackendMonitoringRule.DefaultJson)).Single().MatchingRule?.Severity);
    }

    [TestMethod]
    public void WindowExpiresWithoutAnyNewRequests_AndHealthyObservationDoesNotClearHistory()
    {
        var clock = new ManualClock(); var monitor = new BackendMonitor(clock); var backend = Backend();
        for (var i = 0; i < 6; i++) monitor.Failure(backend);
        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        monitor.Health(backend, true);
        var rules = BackendMonitoringRule.Parse(BackendMonitoringRule.DefaultJson);
        Assert.AreEqual("sev3", monitor.Snapshot(rules).Single().MatchingRule?.Severity);
        Assert.IsFalse(monitor.Snapshot(rules).Single().CanRecover);
        clock.Advance(TimeSpan.FromSeconds(1));
        var state = monitor.Snapshot(rules).Single();
        Assert.AreEqual(0L, state.FailuresInTenMinutes);
        Assert.IsNull(state.MatchingRule);
        Assert.IsTrue(state.CanRecover);
    }

    [TestMethod]
    public void PhysicalIdentity_MergesVirtualMappingsButSeparatesProvidersAndModels()
    {
        var monitor = new BackendMonitor(new ManualClock());
        var a = Backend(); a.Id = 1;
        var b = Backend(); b.Id = 2;
        for (var i = 0; i < 3; i++) { monitor.Failure(a); monitor.Failure(b); }
        monitor.Failure(Backend(2)); monitor.Failure(Backend(1, "other"));
        var states = monitor.Snapshot(BackendMonitoringRule.Parse(BackendMonitoringRule.DefaultJson));
        Assert.HasCount(3, states);
        Assert.AreEqual(6L, states.Single(s => s.Key == new PhysicalModelKey(1, "model")).FailuresInTenMinutes);
        Assert.AreEqual(1, states.Count(s => s.MatchingRule != null));
    }

    [TestMethod]
    public void ContinuousOffline_EscalatesAndFreshSuccessResetsTheTimer()
    {
        var clock = new ManualClock(); var monitor = new BackendMonitor(clock); var backend = Backend();
        var rules = BackendMonitoringRule.Parse(BackendMonitoringRule.DefaultJson);
        monitor.Health(backend, false);
        for (var minute = 1; minute <= 40; minute++)
        {
            clock.Advance(TimeSpan.FromMinutes(1)); monitor.Health(backend, false);
            if (minute == 9) Assert.IsNull(monitor.Snapshot(rules).Single().MatchingRule);
            if (minute == 10) Assert.AreEqual("sev3", monitor.Snapshot(rules).Single().MatchingRule?.Severity);
        }
        Assert.AreEqual("sev2", monitor.Snapshot(rules).Single().MatchingRule?.Severity);
        monitor.Health(backend, true);
        Assert.IsNull(monitor.Snapshot(rules).Single().OfflineSince);
        Assert.IsTrue(monitor.Snapshot(rules).Single().CanRecover);
        clock.Advance(TimeSpan.FromSeconds(1)); monitor.Health(backend, false);
        Assert.IsNull(monitor.Snapshot(rules).Single().MatchingRule);
    }

    [TestMethod]
    public void MissedChecksAreUnknown_NotContinuousOfflineOrRecoveryEvidence()
    {
        var clock = new ManualClock(); var monitor = new BackendMonitor(clock); var backend = Backend();
        var rules = BackendMonitoringRule.Parse(BackendMonitoringRule.DefaultJson);
        monitor.Health(backend, false); clock.Advance(TimeSpan.FromHours(1));
        Assert.IsNull(monitor.Snapshot(rules).Single().Healthy);
        Assert.IsNull(monitor.Snapshot(rules).Single().MatchingRule);
        Assert.IsFalse(monitor.Snapshot(rules).Single().CanRecover);
        monitor.Health(backend, false);
        Assert.IsNull(monitor.Snapshot(rules).Single().MatchingRule);
        monitor.Health(backend, true); clock.Advance(TimeSpan.FromMinutes(4));
        Assert.IsFalse(monitor.Snapshot(rules).Single().CanRecover);
    }

    [TestMethod]
    public void EmptyAndDisabledRulesDoNotAlert_AndOldBucketsDoNotReturnAfterWrapping()
    {
        var clock = new ManualClock(); var monitor = new BackendMonitor(clock);
        for (var i = 0; i < 21; i++) monitor.Failure(Backend());
        Assert.IsNull(monitor.Snapshot([]).Single().MatchingRule);
        var rules = BackendMonitoringRule.Parse(BackendMonitoringRule.DefaultJson);
        rules.ForEach(rule => rule.IsEnabled = false);
        Assert.IsNull(monitor.Snapshot(rules).Single().MatchingRule);
        clock.Advance(TimeSpan.FromDays(2)); monitor.Failure(Backend());
        Assert.AreEqual(1L, monitor.Snapshot(rules).Single().FailuresInTenMinutes);
        monitor.Retain(new HashSet<PhysicalModelKey>());
        Assert.IsEmpty(monitor.Snapshot(rules));
    }

    [TestMethod]
    [DataRow("bad")]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[{\"Minutes\":0}]")]
    [DataRow("[{\"Minutes\":61}]")]
    [DataRow("[{\"Severity\":\"sev5\"}]")]
    [DataRow("[{\"FailureCount\":-1}]")]
    [DataRow("[{\"Kind\":\"invalid\"}]")]
    public void InvalidRulesAreRejected(string value)
    {
        try { BackendMonitoringRule.Parse(value); Assert.Fail("Invalid rules must be rejected."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
    }

    [TestMethod]
    public void DuplicateRulesAndMoreThanTwentyRulesAreRejected()
    {
        var rule = new BackendMonitoringRule();
        Assert.ThrowsExactly<InvalidOperationException>(() => BackendMonitoringRule.Parse(JsonSerializer.Serialize(new[] { rule, rule })));
        Assert.ThrowsExactly<InvalidOperationException>(() => BackendMonitoringRule.Parse(JsonSerializer.Serialize(Enumerable.Range(0, 21).Select(_ => new BackendMonitoringRule()))));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedBackendBody_CountsOnce_ExceptWhenClientCanceled(bool clientCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        var failures = 0;
        using var content = FailureTrackingContent.Wrap(new StreamContent(new BrokenUpstreamStream()), () => failures++, cancellation.Token);
        if (clientCanceled) cancellation.Cancel();
        await using var stream = await content.ReadAsStreamAsync();
        for (var i = 0; i < 2; i++)
            try { await stream.ReadExactlyAsync(new byte[10]); Assert.Fail("The upstream stream must fail."); }
            catch (IOException) { }
        Assert.AreEqual(clientCanceled ? 0 : 1, failures);
    }

    [TestMethod]
    public async Task SuccessfulBody_PreservesHeadersAndContentsWithoutCountingFailure()
    {
        var failures = 0;
        using var content = FailureTrackingContent.Wrap(new StringContent("model response", Encoding.UTF8, "application/json"), () => failures++, CancellationToken.None);
        Assert.AreEqual("application/json", content.Headers.ContentType?.MediaType);
        Assert.AreEqual("model response", await content.ReadAsStringAsync());
        Assert.AreEqual(0, failures);
    }

    [TestMethod]
    [DataRow("{\"done\":true}\n", BackendProtocol.OllamaNative, 0)]
    [DataRow("{\"message\":{\"content\":\"partial\"}}\n", BackendProtocol.OllamaNative, 1)]
    [DataRow("data: [DONE]\n\n", BackendProtocol.OpenAiChatCompletions, 0)]
    [DataRow("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n", BackendProtocol.OpenAiChatCompletions, 1)]
    [DataRow("data: {\"type\":\"response.completed\"}\n\n", BackendProtocol.OpenAiResponses, 0)]
    [DataRow("data: {\"type\":\"response.failed\"}\n\n", BackendProtocol.OpenAiResponses, 1)]
    [DataRow("{\"error\":\"model failed\"}\n{\"done\":true}\n", BackendProtocol.OllamaNative, 1)]
    public async Task StreamingCompletionAndErrorRecords_AreObservedWithoutChangingThePayload(string payload, BackendProtocol protocol, int expected)
    {
        var failures = 0;
        using var content = FailureTrackingContent.Wrap(new StringContent(payload), () => failures++, CancellationToken.None, protocol);
        Assert.AreEqual(payload, await content.ReadAsStringAsync());
        Assert.AreEqual(expected, failures);
    }

    [TestMethod]
    [DataRow("event: response.completed\n", 0)]
    [DataRow("event: response.failed\n", 1)]
    [DataRow("", 0)]
    public async Task OversizedProviderRecords_DoNotCreateFalseTruncationAlerts(string prefix, int expected)
    {
        var payload = prefix + "data: {\"type\":\"response.completed\",\"output\":\"" + new string('x', 100000) + "\"}\n\n";
        var failures = 0;
        using var content = FailureTrackingContent.Wrap(new StringContent(payload), () => failures++, CancellationToken.None, BackendProtocol.OpenAiResponses);
        Assert.AreEqual(payload, await content.ReadAsStringAsync());
        Assert.AreEqual(expected, failures);
    }

    private sealed class BrokenUpstreamStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Provider disconnected.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Provider disconnected."));
    }
}

[TestClass]
public sealed class BackendMonitoringIntegrationTests : TestBase
{
    private const string Key = "synthetic-monitoring-key-not-a-real-credential";

    private async Task<(BackendMonitoringService service, BackendMonitor monitor, BackendMonitoringTests.ManualClock clock,
        RecordingIncidents incidents, VirtualModelBackend backend)> SetupMonitoring()
    {
        var settings = GetService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(SettingsMap.AlertCenterEnabled, "True");
        await settings.UpdateSettingAsync(SettingsMap.AlertCenterApiKey, Key);
        var clock = new BackendMonitoringTests.ManualClock();
        var monitor = new BackendMonitor(clock);
        var db = GetService<TemplateDbContext>();
        var backend = BackendMonitoringTests.Backend();
        backend.Provider!.Id = 0;
        backend.ProviderId = 0;
        backend.VirtualModel = new VirtualModel { Name = "monitoring-test" };
        db.VirtualModelBackends.Add(backend); await db.SaveChangesAsync();
        var incidents = new RecordingIncidents();
        var service = new BackendMonitoringService(monitor, new BackendMonitoringSettings(settings), db, incidents, clock, NullLogger<BackendMonitoringService>.Instance);
        return (service, monitor, clock, incidents, backend);
    }

    [TestMethod]
    public async Task Incidents_CreateDeduplicateEscalateRecreateAfterManualClosureAndMitigate()
    {
        var (service, monitor, clock, incidents, backend) = await SetupMonitoring();
        monitor.Health(backend, true);
        for (var i = 0; i < 6; i++) monitor.Failure(backend);
        await service.ExecuteAsync(); await service.ExecuteAsync();
        Assert.HasCount(1, incidents.All); Assert.AreEqual("sev3", incidents.All.Single().Level);
        StringAssert.Contains(incidents.All[0].Content!, "Other fallback backends may still be serving traffic");
        StringAssert.Contains(incidents.All[0].Content!, "/Providers/Edit/");
        Assert.IsFalse(incidents.All[0].Content!.Contains(Key, StringComparison.Ordinal));
        for (var i = 6; i < 21; i++) monitor.Failure(backend);
        await service.ExecuteAsync(); Assert.AreEqual("sev2", incidents.All[0].Level);
        Assert.AreEqual(1, incidents.Escalations);
        incidents.All[0].Status = "resolved";
        await service.ExecuteAsync(); Assert.HasCount(2, incidents.All);
        clock.Advance(TimeSpan.FromMinutes(10)); monitor.Health(backend, true);
        await service.ExecuteAsync(); Assert.AreEqual("mitigated", incidents.All[1].Status);
        Assert.AreEqual("resolved", incidents.All[0].Status);
        Assert.AreEqual(1, incidents.Mitigations);
    }

    [TestMethod]
    public async Task LostCreateResponse_IsReconciledFromQuery_WithoutDuplicateDelivery()
    {
        var (service, monitor, _, incidents, backend) = await SetupMonitoring();
        incidents.ThrowAfterCreate = true;
        for (var i = 0; i < 6; i++) monitor.Failure(backend);
        await service.ExecuteAsync(); await service.ExecuteAsync();
        Assert.HasCount(1, incidents.All); Assert.AreEqual(1, incidents.CreateCalls);
    }

    [TestMethod]
    public async Task RestartDoesNotMitigateUntilFullObservationWindow_AndDisabledIntegrationMakesNoCalls()
    {
        var (service, monitor, clock, incidents, backend) = await SetupMonitoring();
        for (var i = 0; i < 6; i++) monitor.Failure(backend);
        await service.ExecuteAsync();
        var restarted = new BackendMonitor(clock);
        var settings = GetService<GlobalSettingsService>();
        var replacement = new BackendMonitoringService(restarted, new BackendMonitoringSettings(settings), GetService<TemplateDbContext>(), incidents, clock, NullLogger<BackendMonitoringService>.Instance);
        restarted.Health(backend, true); await replacement.ExecuteAsync();
        Assert.AreEqual("new", incidents.All.Single().Status);
        clock.Advance(TimeSpan.FromMinutes(10)); restarted.Health(backend, true);
        await replacement.ExecuteAsync(); Assert.AreEqual("mitigated", incidents.All.Single().Status);
        await settings.UpdateSettingAsync(SettingsMap.AlertCenterEnabled, "False");
        var calls = incidents.Queries; await service.ExecuteAsync(); Assert.AreEqual(calls, incidents.Queries);
    }

    [TestMethod]
    public async Task AvailableButUnloadedModel_IsNotOffline()
    {
        var (_, monitor, _, _, backend) = await SetupMonitoring();
        TestStartup.OllamaStub.DetailedModels = [new OllamaService.OllamaModel { Name = backend.UnderlyingModelName }];
        TestStartup.OllamaStub.RunningModels = [];
        var job = new BackendHealthMonitor(GetService<TemplateDbContext>(), TestStartup.OllamaStub,
            NullLogger<BackendHealthMonitor>.Instance, monitor);
        await job.ExecuteAsync();
        Assert.IsTrue(monitor.Snapshot([]).Single(state => state.Key == new PhysicalModelKey(backend.ProviderId, backend.UnderlyingModelName)).Healthy);
        Assert.IsNull(monitor.Snapshot([]).Single(state => state.Key == new PhysicalModelKey(backend.ProviderId, backend.UnderlyingModelName)).OfflineSince);
        Assert.IsFalse(backend.IsReady);
    }

    [TestMethod]
    public async Task RulesPage_EnforcesPermissionAndNeverReturnsTheKey()
    {
        var (email, password) = await RegisterAndLoginAsync();
        AssertRedirect(await Http.GetAsync("/AlertMonitoring"), "/Error/Code403", exact: false);
        await GrantPermissionToUser(email, AppPermissionNames.CanManageAlertCenterIntegration);
        await Http.GetAsync("/Account/LogOff");
        await PostForm("/Account/Login", new Dictionary<string, string> { ["EmailOrUserName"] = email, ["Password"] = password });
        var settings = GetService<GlobalSettingsService>();
        await settings.UpdateSettingAsync(SettingsMap.AlertCenterApiKey, Key);
        var response = await Http.GetAsync("/AlertMonitoring");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "More than 5 means the sixth failure");
        Assert.IsFalse(html.Contains(Key, StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("sev@level", StringComparison.Ordinal));
        StringAssert.Contains(html, "value=\"sev3\"");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.PostAsync("/AlertMonitoring/SaveRecovery", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        var version = Regex.Match(html, "name=\"version\" value=\"([^\"]+)\"").Groups[1].Value;
        var data = new Dictionary<string, string> { ["version"] = version, ["kind"] = "failures", ["minutes"] = "3", ["failureCount"] = "2", ["severity"] = "sev4", ["isEnabled"] = "true" };
        AssertRedirect(await PostForm("/AlertMonitoring/SaveRule", data, "/AlertMonitoring"), "/AlertMonitoring");
        Assert.AreEqual(HttpStatusCode.Conflict, (await PostForm("/AlertMonitoring/SaveRule", data, "/AlertMonitoring")).StatusCode);
        Assert.HasCount(5, BackendMonitoringRule.Parse(await settings.GetSettingValueAsync(SettingsMap.AlertCenterRules)));
        AssertRedirect(await PostForm("/AlertMonitoring/SaveRecovery", new Dictionary<string, string>(), "/AlertMonitoring"), "/AlertMonitoring");
        Assert.IsFalse(await settings.GetBoolSettingAsync(SettingsMap.AlertCenterAutoMitigate));
    }

    [TestMethod]
    [DataRow(200, 0)]
    [DataRow(400, 0)]
    [DataRow(404, 0)]
    [DataRow(401, 1)]
    [DataRow(403, 1)]
    [DataRow(429, 1)]
    [DataRow(500, 1)]
    [DataRow(503, 1)]
    public async Task ActualBackendAttempts_ClassifyUpstreamFailuresWithoutCountingClientErrors(int status, int expected)
    {
        var (_, monitor, _, _, backend) = await SetupMonitoring();
        MockUpstreamState.Handler = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") });
        var invoker = new BackendInvoker(GetService<IHttpClientFactory>(), new ModelSelector(),
            new BackendCapabilityPlanner(), new ProviderConcurrencyLimiter(),
            GetService<GatewayRequestTracker>(), NullLogger<BackendInvoker>.Instance, monitor);
        var model = backend.VirtualModel!;
        model.MaxRetries = 1;
        await using var result = await invoker.SendAsync(model, backend, GatewayCapability.ChatCompletion,
            _ => new HttpRequestMessage(HttpMethod.Post, "https://provider.test/api/chat"), CancellationToken.None);
        Assert.AreEqual(expected, monitor.Snapshot([]).SingleOrDefault()?.FailuresInTenMinutes ?? 0);
        MockUpstreamState.Reset();
    }

    [TestMethod]
    [DataRow("network", 1)]
    [DataRow("backend-timeout", 1)]
    [DataRow("client-cancel", 0)]
    public async Task ActualTransportFailures_ExcludeClientCancellation(string scenario, int expected)
    {
        var (_, monitor, _, _, backend) = await SetupMonitoring();
        using var cancellation = new CancellationTokenSource();
        if (scenario == "client-cancel") cancellation.Cancel();
        MockUpstreamState.Handler = (_, _) =>
        {
            return Task.FromException<HttpResponseMessage>(scenario == "network"
                ? new HttpRequestException("Synthetic provider network failure.") : new TaskCanceledException("Synthetic timeout or cancellation."));
        };
        var invoker = new BackendInvoker(GetService<IHttpClientFactory>(), new ModelSelector(),
            new BackendCapabilityPlanner(), new ProviderConcurrencyLimiter(), GetService<GatewayRequestTracker>(),
            NullLogger<BackendInvoker>.Instance, monitor);
        backend.VirtualModel!.MaxRetries = 1;
        try
        {
            await using var result = await invoker.SendAsync(backend.VirtualModel, backend, GatewayCapability.ChatCompletion,
                _ => new HttpRequestMessage(HttpMethod.Post, "https://provider.test/api/chat"), cancellation.Token);
            Assert.AreNotEqual("client-cancel", scenario);
        }
        catch (OperationCanceledException) when (scenario == "client-cancel") { }
        Assert.AreEqual(expected, monitor.Snapshot([]).SingleOrDefault()?.FailuresInTenMinutes ?? 0);
        MockUpstreamState.Reset();
    }

    private sealed class RecordingIncidents : IMonitoringIncidentClient
    {
        public List<IncidentDetails> All { get; } = [];
        public int Queries { get; private set; }
        public int CreateCalls { get; private set; }
        public int Escalations { get; private set; }
        public int Mitigations { get; private set; }
        public bool ThrowAfterCreate { get; set; }
        public Task<QueryIncidentsResponse> QueryAsync(QueryIncidentsRequest request)
        {
            Queries++;
            Assert.IsTrue(request.ActiveOnly);
            Assert.AreEqual("source", request.MetadataKey);
            Assert.AreEqual(BackendMonitoringService.Source, request.MetadataValue);
            var active = All.Where(i => i.Status is not ("mitigated" or "resolved")).ToList();
            return Task.FromResult(new QueryIncidentsResponse { Items = active, TotalCount = active.Count, Page = request.Page, PageSize = request.PageSize });
        }
        public Task<CreateIncidentResponse> CreateAsync(CreateIncidentRequest request)
        {
            CreateCalls++;
            All.Add(new IncidentDetails { IncidentId = Guid.NewGuid(), Version = Guid.NewGuid(), ReportId = request.ReportId,
                Title = request.Title, Content = request.Content, Level = request.Level, Status = "new", Metadata = request.Metadata!, ReceivedAtUtc = DateTimeOffset.UtcNow });
            if (ThrowAfterCreate) throw new HttpRequestException("Synthetic lost response after creation.");
            return Task.FromResult(new CreateIncidentResponse());
        }
        public Task<ChangeIncidentSeverityResponse> ChangeSeverityAsync(Guid id, ChangeIncidentSeverityRequest request)
        {
            var incident = All.Single(i => i.IncidentId == id); Assert.AreEqual(incident.Version, request.Version);
            incident.Level = request.Level; incident.Version = Guid.NewGuid(); Escalations++;
            return Task.FromResult(new ChangeIncidentSeverityResponse());
        }
        public Task<MitigateIncidentResponse> MitigateAsync(Guid id, MitigateIncidentRequest request)
        {
            var incident = All.Single(i => i.IncidentId == id); Assert.AreEqual(incident.Version, request.Version);
            incident.Status = "mitigated"; incident.Version = Guid.NewGuid(); Mitigations++;
            return Task.FromResult(new MitigateIncidentResponse());
        }
    }
}
