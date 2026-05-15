# OllamaGateway 限流架构与设计文档

> **本文档目标**：让未来的工程师在接手本项目时，理解核心架构的设计理由、掌握关键约束、避免引入已知类别的 Bug。

## 核心规则速查

修改本项目代码前，务必理解以下规则：

1. **BackendInvoker 是唯一的后端请求通道**。所有 HTTP 调用、重试、熔断、并发限流逻辑只在此处维护。不要在 Controller 中绕过它直接发 HTTP 请求。
2. **信号量槽位释放是硬约束**。BackendInvoker 中 `concurrencySlot` 在任何一个代码路径上都必须被释放。新增 catch 分支时检查：我释放槽位了吗？
3. **异常过滤器不能跳过清理代码**。`catch (Exception ex) when (condition)` 中，如果 condition 可能为 false，清理代码必须移到过滤器外面或以其他方式保证执行。
4. **中层格式翻译不做抽象**。3 种 API 协议 × 2 种后端类型的翻译路径之间没有共同的接口，不要尝试定义 `ITranslator`。
5. **单例资源要考虑毒化场景**。信号量、熔断器状态是进程级单例，一旦进入错误状态需要重启才能恢复。写代码时问自己：如果这段逻辑出 bug 导致状态异常，服务需要重启吗？

## 目录

1. [为什么需要限流？](#1-为什么需要限流)
2. [两层限流：各自的职责](#2-两层限流各自的职责)
3. [架构分层：前置 → 中部 → 底层](#3-架构分层前置--中部--底层)
4. [完整请求链路](#4-完整请求链路)
5. [Provider 并发限流：信号量机制详解](#5-provider-并发限流信号量机制详解)
6. [API Key 限流：滑动窗口机制详解](#6-api-key-限流滑动窗口机制详解)
7. [已修复的 Bug：信号量泄漏](#7-已修复的-bug信号量泄漏)
8. [如何避免类似问题](#8-如何避免类似问题)
9. [相关文件索引](#9-相关文件索引)

---

## 1. 为什么需要限流？

### 没有限流时的问题

在引入 `MaxParallelism` 之前，存在一个连锁故障场景：

```
用户发送 5 个并发请求
  → 5 个请求同时打到同一个 Ollama 后端
    → Ollama 是单模型推理，无法并行处理
      → 5 个请求全部超时
        → BackendInvoker 调用 5 次 ReportFailure
          → 熔断器在 3 次失败后 ban 掉该后端
            → 后端明明是健康的（只是过载），却被永久踢出
```

**本质矛盾**：熔断器把"后端慢了"当成了"后端坏了"。并发限流的目的是在它们之间加一层缓冲——让请求排队，而不是一起超时。

### 设计目标

- **保护后端**：防止并发请求压垮推理服务
- **保护熔断器精度**：不让"忙碌"被误判为"故障"
- **保护网关**：防止单个 API Key 滥用配额
- **分层解耦**：每层限流保护不同的资源，各管各的

---

## 2. 两层限流：各自的职责

OllamaGateway 有两层独立的限流机制，保护不同的资源：

|   | API Key 限流 | Provider 并发限流 |
|---|---|---|
| **保护对象** | 网关本身 | 后端推理服务 |
| **限制粒度** | 每个 API Key | 每个 Provider（Ollama/OpenAI 实例） |
| **限制指标** | 时间窗口内的请求次数 | 同时进行中的请求数 |
| **实现** | 滑动窗口 + `ConcurrentQueue<DateTime>` | `ConcurrentDictionary<int, SemaphoreSlim>` |
| **超限行为** | 返回 429 或排队等待 (hang) | `SemaphoreSlim.WaitAsync` 排队等待 |
| **配置位置** | `ApiKey` 表 | `OllamaProviders` 表 |
| **默认状态** | 关闭 (`RateLimitEnabled = false`) | 关闭 (`MaxParallelism = 0`) |
| **执行阶段** | 认证阶段（Controller 执行前） | 后端调用阶段（Controller 执行中） |

### 为什么是两层而不是一层？

因为保护的是不同的东西：

- **API Key 限流**保护**网关**不被滥用。如果你给外部用户发了 API Key，你不想一个 key 每秒打 1000 个请求把网关 CPU 打满。
- **Provider 并发限流**保护**后端**不被压垮。一个 Ollama 实例同一时刻只能推理一个模型，多出来的请求应该排队而不是全部超时触发熔断。

两者解决的问题不同、限制的维度不同、拒绝的方式不同，不能合并。

---

## 3. 架构分层：前置 → 中部 → 底层

### 为什么这样分层？

OllamaGateway 本质上是一个**多对多协议网关**：

- **多输入**：Ollama API、OpenAI API、Anthropic API（3 种协议）
- **多输出**：Ollama 后端、OpenAI 兼容后端（2 种后端类型）
- **流式 vs 非流式**：每种组合还要分两种模式

如果全写在一个方法里，就是 3×2×2 = 12 个分支，代码爆炸。

### 三层架构

```
┌───────────────────────────────────────┐
│  前置层：认证 + API Key 限流 + 授权    │
│  ──────────────────────────────────── │
│  [RequiresUserOrApiKeyAuth] Attribute │
│  ApiKeyAuthenticationHandler          │
│  职责：验证身份、检查配额、拒绝滥用     │
│  时机：Controller 执行之前             │
├───────────────────────────────────────┤
│  中层：格式翻译 + 模型/后端选择        │
│  ──────────────────────────────────── │
│  ProxyController (Ollama API)         │
│  OpenAIController (OpenAI API)        │
│  AnthropicController (Anthropic API)  │
│  职责：按协议翻译请求/响应             │
│  时机：Controller action 内            │
├───────────────────────────────────────┤
│  底层：HTTP 调用 + 并发限流 + 重试     │
│  ──────────────────────────────────── │
│  BackendInvoker (所有 Controller 共用) │
│  职责：发请求、排队、重试、熔断        │
│  时机：Controller 调用它               │
└───────────────────────────────────────┘
```

### 为什么中层不能进一步抽象？

有人会问：能不能把格式翻译也抽成一个通用层？

**不能，因为翻译路径之间没有共同的抽象**：

```
Ollama入 → OpenAI出：需要拆 tool_calls 对象→字符串，NDJSON→SSE
OpenAI入 → Ollama出：需要拼 tool_calls 字符串→对象，SSE→NDJSON  
Anthropic入 → Ollama/OpenAI出：需要 content block → messages 转换
```

每种翻译路径的逻辑完全不同。如果强行定义一个 `ITranslator` 接口，每个实现的逻辑互不重叠，接口形同虚设——只是把 Controller 的臃肿搬到了另一个类里。

**当前的设计是务实的**：按协议拆 Controller，每个 Controller 内部按后端类型分支。BackendInvoker 抽走了真正共用的部分（HTTP、重试、限流、熔断）。

---

## 4. 完整请求链路

```
HTTP 请求到达
  │
  ▼
Middleware Pipeline（日志、路由、异常处理）
  │
  ▼
┌─────────────────────────────────────┐
│  前置层：认证 + API Key 限流          │
│  ────────────────────────────────── │
│  [RequiresUserOrApiKeyAuth] 触发    │
│  1. Cookie 用户？→ 跳过，直接通过     │
│  2. API Key 用户？                   │
│     a. 查数据库验证 Key 有效性        │
│     b. RateLimitService.IsAllowedAsync() │
│        - 清掉窗口外的旧时间戳          │
│        - 检查当前窗口内请求数          │
│        - 超限 → 429 或 hang 排队      │
│        - 未超限 → 记录时间戳，放行     │
│  3. 匿名用户？→ 检查全局设置          │
└─────────────────────────────────────┘
  │
  ▼
Controller Action 执行
  │
  ├─ 模型选择：dbContext.VirtualModels.Include(...)
  ├─ 后端选择：modelSelector.SelectBackend(virtualModel)
  │   └─ 过滤条件：Enabled && (IsHealthy || IsReady)
  │   └─ 过滤掉被熔断 ban 的后端
  │   └─ 根据策略选择：PriorityFallback / RoundRobin / WeightedRandom
  ├─ 格式翻译：根据输入协议和输出类型翻译请求体
  │
  ▼
┌─────────────────────────────────────┐
│  底层：BackendInvoker.SendAsync()    │
│  ────────────────────────────────── │
│  for (retry = 0; retry < maxRetries) │
│  {                                   │
│    1. 获取并发槽位                     │
│       AcquireAsync(providerId,       │
│         maxParallelism, ct)          │
│       - maxParallelism <= 0 → 不限流  │
│       - maxParallelism > 0  → 信号量  │
│                                      │
│    2. 构造 + 发送 HTTP 请求           │
│                                      │
│    3. 处理响应                        │
│       - 2xx → ReportSuccess → 返回    │
│       - 4xx → ReportSuccess → 返回    │
│       - 5xx → ReportFailure → 重试   │
│       - 异常 → ReportFailure → 重试   │
│  }                                   │
│  返回 BackendInvocationResult        │
│  (内含 HttpResponseMessage +        │
│   后端信息 + 并发槽位)                │
└─────────────────────────────────────┘
  │
  ▼
Controller 使用 BackendInvocationResult
  │
  ├─ 流式：逐行读取 + 翻译 + 转发给客户端
  ├─ 非流式：读全 + 翻译 + 转发
  │
  ▼
await using result.DisposeAsync()
  → 释放信号量槽位
  → 释放 HttpResponseMessage
  │
  ▼
响应返回客户端
```

---

## 5. Provider 并发限流：信号量机制详解

### 核心类

**ProviderConcurrencyLimiter** (`src/.../Services/ProviderConcurrencyLimiter.cs`)

```csharp
// 单例，维护 per-provider 的信号量池
ConcurrentDictionary<int, SemaphoreSlim> _semaphores;

async Task<IAsyncDisposable> AcquireAsync(int providerId, int maxParallelism, CancellationToken ct)
{
    if (maxParallelism <= 0)
        return NoOpDisposable.Instance;  // 不限流

    var semaphore = _semaphores.GetOrAdd(providerId,
        _ => new SemaphoreSlim(maxParallelism, maxParallelism));

    await semaphore.WaitAsync(ct);       // 排队等待
    return new SemaphoreRelease(semaphore); // dispose时释放
}
```

### 关键设计决策

**1. 排队不上锁，上锁不排队**
- `WaitAsync` 的取消 token 是 `HttpContext.RequestAborted`（客户端断连），不是 `HealthCheckTimeout`（后端健康检查超时）
- 这意味着：排队不会触发超时熔断。只有真正拿到槽位、开始发 HTTP 请求后，超时才会计时

**2. 信号量不随配置热更新**
- `GetOrAdd` 只在第一次创建信号量。后续修改数据库中的 `MaxParallelism` 值不会更新已有的信号量
- 这意味着：修改限流值需要重启应用。这是有意为之——如果运行时把 5 改成 1，已经在飞的 5 个请求无法被强制释放

**3. 不限流时的零开销**
- `maxParallelism <= 0` 直接返回 `NoOpDisposable`，完全不经过信号量

### BackendInvocationResult

```csharp
// 打包对象，Controller 通过 await using 自动管理生命周期
public sealed class BackendInvocationResult : IAsyncDisposable
{
    HttpResponseMessage Response { get; }
    VirtualModelBackend Backend { get; }
    IAsyncDisposable? _concurrencySlot;

    async ValueTask DisposeAsync()
    {
        _concurrencySlot?.DisposeAsync();  // 释放信号量
        Response.Dispose();                 // 释放 HTTP 响应
    }
}
```

---

## 6. API Key 限流：滑动窗口机制详解

### 核心类

**RateLimitService** (`src/.../Services/RateLimitService.cs`)

```csharp
// 单例，per-API-key 的滑动窗口
ConcurrentDictionary<int, ConcurrentQueue<DateTime>> _requestLogs;

async Task<bool> IsAllowedAsync(ApiKey apiKey)
{
    if (!apiKey.RateLimitEnabled) return true;

    var queue = _requestLogs.GetOrAdd(apiKey.Id, _ => new());
    var windowStart = DateTime.UtcNow.AddSeconds(-apiKey.TimeWindowSeconds);

    // 1. 清掉窗口外的旧记录
    while (queue.TryPeek(out var oldest) && oldest < windowStart)
        queue.TryDequeue(out _);

    // 2. 未超限：记录并放行
    if (queue.Count < apiKey.MaxRequests)
    {
        queue.Enqueue(DateTime.UtcNow);
        return true;
    }

    // 3. 超限
    if (!apiKey.RateLimitHang)
        return false;  // 直接拒绝 → 429

    // 4. Hang模式：等到有槽位
    while (queue.TryPeek(out var oldest) && queue.Count >= apiKey.MaxRequests)
    {
        var waitMs = (int)(oldest - windowStart).TotalMilliseconds;
        if (waitMs > 0) await Task.Delay(waitMs);
        // 再次清理窗口外的记录
        while (queue.TryPeek(out var t) && t < DateTime.UtcNow.AddSeconds(-apiKey.TimeWindowSeconds))
            queue.TryDequeue(out _);
    }

    queue.Enqueue(DateTime.UtcNow);
    return true;
}
```

### 两种模式

| 模式 | `RateLimitHang` | 行为 |
|---|---|---|
| 直接拒绝 | `false` | 超限立即返回 429 Too Many Requests |
| 排队等待 | `true` (默认) | 阻塞等待直到窗口内有空位 |

---

## 7. 已修复的 Bug：信号量泄漏

### 症状

```
MaxParallelism = 1
↓
第一个请求获取槽位 → 客户端断开连接
↓
信号量槽位永久泄漏（0/1 可用）
↓
所有后续请求在 WaitAsync 上无限排队
↓
Ollama 后端空闲，但请求永远到不了
```

### 根因

`BackendInvoker.cs` 原来的异常处理代码：

```csharp
// BUG: 异常过滤器故意放过客户端取消异常，但跳过了槽位释放
catch (Exception ex) when (
    ex is not OperationCanceledException ||
    !clientCancellation.IsCancellationRequested)
{
    await concurrencySlot.DisposeAsync();  // ← 这一行在客户端取消时被跳过
    modelSelector.ReportFailure(backend!.Id);
    // ... retry logic ...
}

// 异常直接传播出去，post-loop 清理也到不了
if (concurrencySlot != null)
    await concurrencySlot.DisposeAsync();
```

**逻辑**：如果客户端断开了（`clientCancellation.IsCancellationRequested == true`），不想重试，直接让异常传播出去快速结束。**但忘了释放槽位**。

### 为什么难以发现

1. **只在 MaxParallelism=1 时触发**：没限流时不经过信号量，不限流时没有这个 bug
2. **需要客户端断开的时机恰好**：在 HTTP 请求发出后、响应头返回前断开。如果响应头已经返回，槽位由 Controller 的 `await using` 管理，不会泄漏
3. **一次泄漏就永久阻塞**：信号量是单例，进程不重启永远不会恢复
4. **症状误导**：看起来像是 Ollama 挂了或响应卡住了

### 修复方案

把原来的**带过滤器的单个 catch** 拆成**两个独立的 catch**：

```csharp
// 1. 客户端取消：释放槽位，但不报告熔断（后端无责）
catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested)
{
    if (concurrencySlot != null)
    {
        await concurrencySlot.DisposeAsync();
        concurrencySlot = null;
    }
    throw;  // 传播异常让 Controller 处理
}

// 2. 真正失败：释放槽位，报告熔断，重试
catch (Exception ex)
{
    if (concurrencySlot != null)
    {
        await concurrencySlot.DisposeAsync();
        concurrencySlot = null;
    }
    modelSelector.ReportFailure(backend!.Id);
    // ... retry logic ...
}
```

### 配套修复

- **扩大 try 块覆盖范围**：将 AcquireAsync 之后的所有代码（包括 `GetRequestTimeoutAsync`、`requestFactory` 等）都纳入同一个 try 块，防止这些中间步骤抛异常时槽位泄漏
- **每次释放后置 null**：所有 catch 块和 5xx 重试路径中，释放槽位后立即 `concurrencySlot = null`，防止循环后双重释放

### 验证

`tests/ProviderConcurrencyLimiterTests.cs` 新增两个测试：

| 测试 | 作用 |
|---|---|
| `LeakedSemaphore_PermanentlyBlocksSubsequentRequests` | 验证泄漏的槽位永久阻塞后续请求 |
| `ProperlyDisposedSlot_AllowsSubsequentRequests` | 对照组：正常释放后可以获取 |

全部 239 个测试通过。

---

## 8. 如何避免类似问题

### 原则 1：资源获取和释放必须结构化

**反面模式**（修复前）：

```csharp
concurrencySlot = await AcquireAsync(...);  // 获取资源

// 一大段代码，多路径可能抛异常

await concurrencySlot.DisposeAsync();       // 某条路径上忘了释放
```

**正面模式**（修复后）：

```csharp
// 所有可能抛异常的代码都在同一个 try 内，catch 全方位覆盖
try
{
    concurrencySlot = await AcquireAsync(...);
    // ... 所有使用槽位的代码 ...
}
catch (Exception1) { Release(); ... }
catch (Exception2) { Release(); ... }
// 兜底
finally { if (concurrencySlot != null) Release(); }
```

在 C# 中，优先使用 `await using` 自动管理（如 `BackendInvocationResult` 那样）。当无法使用 `await using` 时（如本场景中槽位需要跨方法传递），确保获取和释放之间有严格的 try-catch-finally 守卫。

### 原则 2：异常过滤器不要跳过清理代码

```csharp
// 危险：过滤器 return false 时，catch 体内的清理不执行
catch (Exception ex) when (SomeCondition)
{
    Cleanup();  // SomeCondition 为 false 时，Cleanup 被跳过
}

// 安全：用两个独立的 catch，各自完成清理
catch (SpecificException) when (ConditionA)
{
    Cleanup();
    throw;
}
catch (Exception)
{
    Cleanup();
    // ...
}
```

### 原则 3：信号量/锁等稀缺资源的分配必须可证空

- `concurrencySlot` 在每次释放后立即置 null
- 循环后兜底清理检查 `!= null` 才执行
- 这保证了不会双重释放，也不会遗漏释放

### 原则 4：TDD 验证泄漏场景

`LeakedSemaphore_PermanentlyBlocksSubsequentRequests` 测试直接模拟了泄漏场景：

```csharp
var leakedSlot = await limiter.AcquireAsync(1, maxParallelism: 1, CancellationToken.None);
// 不释放 leakedSlot

// 验证：后续请求永久阻塞
using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
try
{
    await limiter.AcquireAsync(1, maxParallelism: 1, cts.Token);
    // 如果到达这里，说明槽位泄漏没有正确阻塞 → 测试失败
}
catch (OperationCanceledException) { /* 预期行为 */ }
```

这类测试直接验证了"如果一个槽位泄漏，系统会怎样"，无论泄漏根因是什么。

### 原则 5：单例状态要考虑"毒化"场景

信号量是单例（进程存活期间一直存在）。一旦被"毒化"（槽位泄漏），整个进程的所有后续请求都受影响。对于单例持有的关键资源，需要考虑：

- 槽位泄漏时是否有监控告警？
- 是否有超时机制？当前：`WaitAsync(clientCancellation)` — 依赖客户端超时，服务端没有主动超时
- 是否需要支持运行时重置？当前：只能重启

### 反模式清单（禁止事项）

| 禁止行为 | 原因 | 正确做法 |
|---|---|---|
| 在 Controller 中直接创建 HttpClient 调用后端 | 绕过 BackendInvoker 的限流、重试、熔断 | 始终通过 `backendInvoker.SendAsync()` |
| 在 `catch (Exception ex) when (condition)` 中放清理代码 | condition=false 时清理代码被跳过 | 拆成两个独立 catch，各自清理 |
| 释放槽位后不立即置 null | 循环后兜底清理会双重释放 → `SemaphoreFullException` | `DisposeAsync(); concurrencySlot = null;` |
| 用 HealthCheckTimeout 的 CTS 去取消 AcquireAsync | 排队时间会计入健康检查，排队本身就触发熔断 | AcquireAsync 只用 `clientCancellation` |
| 试图定义 `ITranslator` 统一格式翻译 | 翻译路径之间没有共同抽象，接口形同虚设 | 按协议拆 Controller，内部按后端类型分支 |
| 假设信号量会在数据库配置更新后自动调整 | `GetOrAdd` 不会更新已有 SemaphoreSlim | 文档说明：修改 MaxParallelism 需要重启 |
| 在 `ReportFailure` 后忘记检查 `backend?.Provider == null` | SelectBackend 可能返回 null（所有后端都被 ban） | 每处 `SelectBackend` 后都检查 null |

### 修改 BackendInvoker 的检查清单

修改 `BackendInvoker.SendAsync()` 时，逐项确认：

- [ ] 新增的代码路径中，`concurrencySlot` 是否在所有 exit 点都被释放？
- [ ] 新增的 catch 块中，是否包含了 `concurrencySlot = null`？
- [ ] 新增的 return 语句中，如果是返回 `BackendInvocationResult`，是否把 `concurrencySlot` 传了进去？
- [ ] 新增的 return null 路径前，是否调用了 `concurrencySlot.DisposeAsync()`？
- [ ] 新增的 continue 前，是否释放了当前槽位并置 null？
- [ ] 是否在 `AcquireAsync` 和 `try` 之间插入了可能抛异常的代码？（这是未保护区）
- [ ] 是否通过了 `ProviderConcurrencyLimiterTests` 全部 7 个测试？
- [ ] 是否在 `MaxParallelism = 1` 的场景下手动测试了客户端断连？

---

## 9. 相关文件索引

| 文件 | 职责 |
|---|---|
| `src/.../Services/BackendInvoker.cs` | HTTP调用 + 并发限流 + 重试 + 熔断的编排 |
| `src/.../Services/ProviderConcurrencyLimiter.cs` | 信号量池，槽位获取/释放 |
| `src/.../Services/BackendInvocationResult.cs` | 打包 HttpResponseMessage + Backend + 槽位，IAsyncDisposable |
| `src/.../Services/ModelSelector.cs` | 后端选择 + 熔断器（3次失败 ban，指数退避 5^n 分钟） |
| `src/.../Services/RateLimitService.cs` | API Key 滑动窗口限流 |
| `src/.../Authorization/ApiKey/ApiKeyAuthenticationHandler.cs` | API Key 认证 + 限流触发点 |
| `src/.../Authorization/RequiresUserOrApiKeyAuthAttribute.cs` | 认证/授权过滤器 |
| `src/.../Controllers/ProxyController.cs` | Ollama API 入口 + Ollama↔OpenAI 格式翻译 |
| `src/.../Controllers/OpenAIController.cs` | OpenAI API 入口 + OpenAI↔Ollama 格式翻译 |
| `src/.../Controllers/AnthropicController.cs` | Anthropic API 入口 + Anthropic↔OpenAI/Ollama 格式翻译 |
| `src/.../Entities/ApiKey.cs` | API Key 实体，含限流配置字段 |
| `src/.../Entities/OllamaProvider.cs` | Provider 实体，含 `MaxParallelism` 字段 |
| `tests/ProviderConcurrencyLimiterTests.cs` | 信号量机制测试，含泄漏验证 |
| `tests/ModelSelectorTests.cs` | 熔断器测试，含并发场景 |
