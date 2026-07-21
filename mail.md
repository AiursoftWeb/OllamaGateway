# OllamaGateway 架构重构 — 2026-07-21

## 起因：一个把 GPU 炸了一晚上的恶性 Bug

我们的 ollama.aiursoft.com 挂着一个 27GB 的 Qwen 大模型（qwen3.6:27b-q8_0）。某天用户反馈：**"模型永远用不了，一直转圈，服务器风扇狂转一晚上。"**

### 案发过程还原

```
时间线（每次循环一模一样）：

T+0s    第一个用户请求到达
        客户端 → OllamaGateway → Ollama 后端：POST /api/chat
        Ollama 收到请求：模型不在显存里！开始从磁盘读 27GB...

        同时，Gateway 里两把刀开始倒计时：
          • 刀 A: client.Timeout = RequestTimeoutInMinutes（全局设置，5 分钟）
          • 刀 B: cts.CancelAfter = HealthCheckTimeout（虚拟模型字段，300 秒）
        两个都是 5 分钟，同一个请求，两把刀同时砍。

T+1s    第二个请求来了 → MaxParallelism=1，没有空闲槽，排队
T+2s    第三个请求来了 → 继续排队
        ...后面所有请求全在排队...

        此时 Ollama 还在读盘：24GB/27GB... 25GB/27GB... 26GB/27GB...

T+299s  就快加载完了！就差最后一点！

T+300s  两把刀同时落下。
        Gateway 主动断开 TCP 连接。
        Ollama 视角：客户端跑路了？那我白搬了 27GB？
        → aborting load，已加载的数据全废，显存清空。

        BackendInvoker 捕获超时异常 → ReportFailure → SelectBackend 选新后端。
        但只有这一个 Provider，选出来的还是它。

T+301s  重试！POST /api/chat → Ollama。
        两把刀重新开始倒计时：300s...
        Ollama 重新从磁盘读 27GB...

T+601s  又砍！ReportFailure 再 +1。
T+901s  再砍！ReportFailure 攒够 3 票 → 熔断器 Ban 该后端 1 分钟。
        此刻所有请求全部失败。

        BackendHealthMonitor 后台探活也超时 → IsHealthy = false, IsReady = false。
        熔断器 + 健康监控，双重死刑。

        用户视角：所有请求挂着转圈，最后全 503。
        管理员视角：Dashboard 飘红，不知道原因。
        Ollama 视角：我一晚上搬运了三次 27GB，全白干了。
        GPU 视角：加载→卸载→加载→卸载→加载→卸载，风扇狂转，零服务。
```

### 这个配置到底在哪？为什么这么乱？

死亡螺旋的根本原因，是**两个不同位置的超时配置同时作用于同一个请求**：

| 当时的配置 | 在哪改 | 当时的值 | 控制什么 |
|-----------|--------|---------|---------|
| `RequestTimeoutInMinutes` | 管理后台 → 全局设置 | 5 分钟 | HttpClient.Timeout |
| `HealthCheckTimeout` | 管理后台 → 虚拟模型 → 编辑 | 300 秒 | CancellationToken.CancelAfter |

**两个独立的配置页面，两个独立的值，掐的是同一件事。** 管理员改了虚拟模型的超时（以为够了），但全局设置的 5 分钟也同时在倒计时。改一个不够，得两个都改——而且没有人告诉管理员有两个。

更糟的是，`MaxParallelism=1` 让后续请求全排队，没有 fallback（只有一个 Provider），所以一个请求卡住，全部卡住。Ollama 每次快要加载完的时候被掐断，前功尽弃，重试又从零开始。

---

## 修 Bug 修出一座屎山

修这个恶性 Bug 的过程中，我们发现整个超时体系已经腐烂到了骨头里。每一个"配置项"的背后都是一个谎言：

### 谎言 1：两个超时互不知情

`BackendInvoker` 同时设置 `client.Timeout = 全局设置` 和 `cts.CancelAfter = 虚拟模型字段`。名字不同，位置不同，但掐的是同一个请求。**管理员在两个页面各改各的，没人知道有两把刀。**

### 谎言 2："排队不扣超时"是假的

`MaxParallelism=1` 时，注释写着"排队不消耗超时"，但 `CancelAfter` 在抢并发槽之前就启动了。排队 300 秒→拿到槽→只剩 0 秒→直接超时。注释欺骗了每一个读代码的人。

### 谎言 3：Test Connection 绿灯，但路由仍拒绝

管理员点 Provider 的"Test Connection"，返回绿色成功。**但熔断器还在 Ban，数据库还是 `IsHealthy=false`，请求全部被拒绝。** 管理员看到绿灯以为修好了，用户还是用不了。这是最恶劣的 UI 欺骗。

### 谎言 4：Warmup 配好了，模型还是冷的

Provider 页面精心配置了 Warmup——选模型、设 NumCtx。但 `keep_alive=1m`，预热每 5 分钟跑一次，模型只活 1 分钟就卸载。**27GB 的模型，80% 时间在重复加载，白白搬运数据。**

---

## 重构后：现在捋顺了吗？

**捋顺了。** 现在只有一把刀，一个地方改。

用户请求走过这些步骤：

```
客户端请求
  → 认证 & API 限流（Hang 模式排队不扣超时）
  → 虚拟模型选择（PriorityFallback 选出优先级最高的后端）
  → Provider 并发槽排队（MaxParallelism=1 时排队，不扣超时）
  → 槽到手！⏱️ 超时计时器启动（只在这里开始倒计时）
  → POST /api/chat → 实体模型（收到响应头 = 超时解除，流式不限时）
```

**排队全部免费。** 无论是 API 限流排队还是并发槽排队，都在超时计时器启动之前。`RequestTimeoutSeconds` 只算真正发给 Ollama 之后的时间。

**Fallback 也清楚了。** 第 1 个后端超时 → 换第 2 个 → 全新计时器，全新 600 秒 → 收到响应头，超时解除。

### 新设计：4 层互不干涉

| 层 | 在哪配置 | 默认值 | 控制什么 |
|---|---------|--------|---------|
| **数据面** | 虚拟模型 → Request Timeout (s) | 600s | 等后端开始回答的最长时间 |
| **控制面** | 提供商 → Health Check Timeout (s) | 60s | 后台探活 `/api/tags` 等多久 |
| **预热面** | 提供商 → Warmup → 每个模型的 Timeout | 1800s | 预热加载等多久 |
| **UI 面** | 硬编码 | 3s | 管理页面快速探活，不写数据库 |

**一刀一个坑，谁也不砍谁。** 管理员只需要去一个地方改一个值。

关键代码改动：
- `HealthCheckTimeout` 重命名为 `RequestTimeoutSeconds`，默认 600s（用 `[Column]` 免迁移）
- `BackendInvoker` 两刀合一，只读虚拟模型字段
- 并发排队时 `cts.CancelAfter` 移到 acquire 之后——排多久都不扣超时
- 全局设置里的 `RequestTimeoutInMinutes` 彻底删除
- Provider 新增 `HealthCheckTimeoutSeconds`，默认 60s，归属实体而非全局设置

---

## Bug 修复清单

1. **Test Connection 成功→复活 Provider**：现在会在成功后自动清除熔断器 Ban + 恢复 `IsHealthy=true, IsReady=true`
2. **并发排队不扣超时**（真的修了，不是改注释）
3. **ModelWarmup 多模型崩溃**：复用同一个 HttpClient 导致 `InvalidOperationException`，改为每模型独立 Client
4. **KeepAlive 帮助文本炸页面**：`{number}{unit}` 被 Localizer 误解析，Create/Edit 页 500

---

## 为什么管理员以前天天在论坛骂我们？

因为我们把"配置"和"真相"割裂了。UI 上有个输入框，底层的代码却读的是另一个值；UI 上显示绿标，底层路由却拒绝请求。**每发现一个问题，我们就加一个 help text、一个 tooltip、一个可视化卡片，直到管理员不用翻代码就能理解系统在干什么。**

### 新增 Architecture Guide 页面

**👉 https://ollama.aiursoft.com/Dashboard/Guide — 欢迎点进去亲自体验。**

8 张 Bootstrap 卡片，用真实数据（从你的默认模型和提供商读出）可视化完整请求流：

客户端 → 认证 & 限流 → 虚拟模型选择 → 并发排队（🏠 小人可视化）→ 超时启动 → 实体模型 → 日志管线 → 4 层超时总览

### 其他 UI 修复

- **VirtualModels 列表**：右侧新增状态指南 Sidebar，解释 Ready / Healthy / Down / Banned 是啥、会不会发请求、模型是否已加载
- **Request Timeout 输入框**：增加 tooltip — 建议值（小模型 60s / 大模型 600s / Embedding 30s）、不计排队和限流、流式不受限
- **MaxParallelism 输入框**：增加提示 — 排队不消耗虚拟模型超时，不会因为 GPU 忙而放弃后端
- **KeepAlive 输入框**：增加 Ollama 格式说明（`5m`、`1h`）和反例警告（不要写 `300`）
- **Provider 列表**：新增并发列（Active/Max + 排队数黄色徽标）和健康检查超时列
- **Chat 按钮**：改为直达 ChatPlayground 并自动预填模型名（之前得手动选模型）

---

## 服务器摸排发现

直接登上 proart 和 DGX Spark 确认了实际运行状态：

1. **Warmup 之前纯属白跑** — `keep_alive=1m`，预热 5 分一次，模型只活 1 分，等于 80% 时间冷着。已改为 5m。
2. **DGX Spark 大材小用** — vLLM 配了 `max-num-seqs=64`，Gateway 只设了 `MaxParallelism=4`，差了 16 倍。
3. **两个虚拟模型超时没更新** — `aiursoft-moog:latest` 和 `aiursoft-super:latest` 的 RequestTimeoutSeconds 还是旧默认值 40s。
4. **Gateway 部署时 Ollama 并没死** — 是容器重启→所有连接断开→keep_alive 到期→模型正常卸载。不是 bug，是行为理解偏差。

---

## 改动量

- 35 个文件（含 2 个 EF 迁移、1 个新页面 Guide、1 个新 ViewModel）
- 279 个测试全部通过
- 6 次提交推送到 master

Anduin
