# OllamaGateway 超时架构重构报告 — 2026-07-21

## 一、故障分析

2026 年 7 月 21 日，ollama.aiursoft.com 发生了一起持续性服务中断。部署于该节点的 Qwen3.6 27B 模型（qwen3.6:27b-q8_0，模型文件大小 29 GB）无法响应任何用户请求，且 GPU 整夜处于高负载状态，未产生有效推理。

### 故障机理

经代码审查与服务器日志交叉验证，故障根因定位为**双重超时控制**导致的**死锁式重试循环**。

系统在处理单次后端请求时，同时应用了两套独立的超时机制：

| 超时 | 配置位置 | 生效方式 | 实际值 |
|------|---------|---------|--------|
| `RequestTimeoutInMinutes` | 全局设置页 | `HttpClient.Timeout` | 5 分钟 |
| `HealthCheckTimeout` | 虚拟模型编辑页 | `CancellationToken.CancelAfter` | 300 秒 |

两套超时分别配置于不同的管理页面，作用于同一个 HTTP 请求，无优先级关系，先到期者胜出。

故障过程如下：

```
T+0      客户端发起请求。Ollama 后端开始从磁盘读取 29 GB 模型文件。
         HttpClient.Timeout 与 CancellationToken 同时开始倒计时。

T+1..299 后续请求到达，因 MaxParallelism=1 进入并发队列排队。
         所有请求阻塞于同一后端。

T+300    两套超时同时到期。Gateway 断开 TCP 连接。
         Ollama 检测到客户端断开，终止加载，释放已申请之显存。
         BackendInvoker 捕获异常，调用 ReportFailure，
         然后 SelectBackend 重新选择后端——但仅有此 Provider，
         故再次选中同一节点。

T+301    重试开始。超时重新设置。模型重新加载。

T+601..901
         重复上述过程。ReportFailure 累计 3 次，
         触发熔断器限制（1 分钟→5 分钟→25 分钟指数退避）。
         BackendHealthMonitor 同步探活超时，
         将 IsHealthy 与 IsReady 均标记为 false。
         此后所有路由均被拒绝，服务完全中断。
```

**结论：管理员无法从任何单一配置页面获知两套超时的共存关系。修改其一不足以解除死锁，此为设计缺陷。**

---

## 二、重构方案

### 设计原则

将超时按请求性质严格分层，每层由唯一归属实体控制，消除多源冲突。

### 四层超时模型

| 层 | 定义 | 归属实体 | 默认值 | 控制范围 |
|---|------|---------|--------|---------|
| 数据面 | 用户请求等待后端开始响应的最大时长 | `VirtualModel.RequestTimeoutSeconds` | 600 s | `BackendInvoker` 内 `HttpClient.Timeout` 与 `CancellationToken.CancelAfter`，二者使用同一值 |
| 控制面 | 后台探活等待 Provider 响应的最大时长 | `OllamaProvider.HealthCheckTimeoutSeconds` | 60 s | `BackendHealthMonitor` 调用 `/api/tags` 与 `/api/ps` |
| 预热面 | 预热任务等待模型加载的最大时长 | `WarmupModel.TimeoutSeconds` | 1800 s（后备值） | `ModelWarmupService`，可按模型独立配置 |
| 展示面 | 管理页面快速探活的最长等待时间 | 硬编码 | 3 s | `OllamaProvidersController`，仅影响 UI 展示，不写入数据库 |

**关键性质：**
- 数据面计时器在 Provider 并发槽获取之后启动。认证、API 限流排队、并发槽排队均不消耗数据面超时。
- `HttpCompletionOption.ResponseHeadersRead`：后端发送 HTTP 响应头即视为超时条件满足，流式输出阶段不受超时限制。仅在客户端断开时终止。
- 每个后端重试获得独立的、完整的超时配额，不共享、不累计。

### 实体变更

- `VirtualModel.HealthCheckTimeout`（40 s）→ `VirtualModel.RequestTimeoutSeconds`（600 s），使用 `[Column("HealthCheckTimeout")]` 保持数据库列名兼容。
- `OllamaProvider` 新增 `HealthCheckTimeoutSeconds`（60 s），替代原全局设置 `RequestTimeoutInMinutes`。
- `WarmupModel` 新增 `TimeoutSeconds`（可选），未配置时使用 1800 s 后备值。
- 全局设置 `RequestTimeoutInMinutes` 及相关方法 `GetRequestTimeoutAsync()` 移除。

---

## 三、并发缺陷修复

### 缺陷 1：并发排队期间超时计时器错误启动

`BackendInvoker.SendAsync()` 中，`CancellationTokenSource.CancelAfter()` 调用位于 `ProviderConcurrencyLimiter.AcquireAsync()` 之前。代码注释声称排队不计入超时，但 `CancelAfter` 在 `AcquireAsync` 阻塞期间持续倒计时。若排队耗时等于或超过超时值，请求在获得并发槽后立即超时。

**修复：** 将 `CancellationTokenSource` 的创建与 `CancelAfter` 调用移至 `AcquireAsync` 返回之后。

### 缺陷 2：Test Connection 成功后状态未同步

Provider 编辑页的 Test Connection 功能验证了 Provider 可达性，但成功时不调用 `ModelSelector.ReportSuccess()` 亦不恢复 `IsHealthy`/`IsReady` 标记。管理员看到绿色成功提示后，路由层仍拒绝向该 Provider 转发请求。

**修复：** Test 方法在成功时查询该 Provider 关联的所有 `VirtualModelBackend`，逐一恢复其健康状态并清除熔断器记录。

### 缺陷 3：ModelWarmup 复用 HttpClient 导致后续模型超时赋值失败

`HttpClient.Timeout` 仅在首次请求前可设置。原代码在 `foreach` 循环外创建单个 `HttpClient` 实例，循环内为每个模型重新赋值 `Timeout`，第二个模型即抛 `InvalidOperationException`。

**修复：** 将 `HttpClient` 创建移入循环内，每个模型获得独立实例。

---

## 四、可观测性与用户体验改进

### 新增 Architecture Guide 页面

**https://ollama.aiursoft.com/Dashboard/Guide** 提供基于真实系统数据的可视化请求流，包含 8 个信息卡片：客户端入口、认证与限流、虚拟模型选择、Provider 并发排队（含实时队列深度）、超时启动条件、后端物理模型、ClickHouse 日志管线、四层超时体系总览。

### 状态语义说明

VirtualModels 列表页右侧新增状态指南面板，定义四种后端状态的精确语义：

| 状态 | 含义 | 是否接受路由 | 模型是否已加载至显存 |
|------|------|:--:|:--:|
| Ready | 模型在 `/api/ps` 中，已在显存 | 是 | 是，即时响应 |
| Healthy | 模型在 `/api/tags` 中，未在显存 | 是 | 否，Ollama 按需加载 |
| Down | 模型不在 Provider 上 | 否 | — |
| Banned | 熔断器因连续失败禁止路由 | 否 | — |

### 表单字段增强

- `RequestTimeoutSeconds` 增加 tooltip，包含建议值范围（小模型 60–120 s，大模型 600–1800 s，Embedding 30–60 s）及不计入项说明（认证、限流排队、并发槽排队、流式输出阶段）。
- `MaxParallelism` 增加提示：并发排队不消耗虚拟模型超时，不因后端忙碌而放弃重试。
- `KeepAlive` 增加 Ollama 时间格式说明（`5m`、`1h`、`24h`），并警告不得使用原始秒数（如 `300`）。
- Provider 列表新增两列：并发状态列（活跃数/上限 + 排队数）和健康检查超时列。
- Chat 快捷按钮改为直接跳转 ChatPlayground 并自动填入模型名。

### Provider 并发队列可视化

`ProviderConcurrencyLimiter` 新增等待计数功能（原子操作，热路径开销可忽略），在 Guide 页面及 Provider 列表中以图形化方式呈现各 Provider 的实时并发状态。

---

## 五、服务器巡检发现

对 proart 服务器及 DGX Spark 节点进行了实地检查，发现以下问题：

1. **Warmup 配置不合理。** `keep_alive` 设为 1 分钟，预热周期为 5 分钟，导致模型在预热间隔的 80% 时间内处于卸载状态，每次均需重新加载 29 GB 数据。已调整为 5 分钟。
2. **并发上限严重偏低。** DGX Spark（vLLM, minimax-m2.7）配置 `max-num-seqs=64`，但 Gateway 侧 `MaxParallelism` 仅设为 4，利用率不足 7%。
3. **两个虚拟模型超时未更新。** `aiursoft-moog:latest` 与 `aiursoft-super:latest` 的 `RequestTimeoutSeconds` 仍为旧默认值 40 秒，远低于推荐的 600 秒。
4. **Ollama 未发生崩溃。** Gateway 容器部署过程中，所有活跃连接断开，`keep_alive` 到期后模型正常卸载。属容器编排的正常行为，非 Ollama 故障。

---

## 六、变更统计

- 修改文件：35 个（含 2 个 EF Core 数据库迁移、1 个新增页面、1 个新增 ViewModel）
- 测试：279 项全部通过
- 推送到 master 分支

Anduin
