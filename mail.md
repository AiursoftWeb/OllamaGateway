# OllamaGateway 架构重构 — 2026-07-21

今晚对 OllamaGateway 进行了一次深度架构重构。核心目标：消灭"配置了但不生效"的谎言，让管理员能在 UI 上自助诊断问题，而不是天天在论坛发帖。

---

## 一、超时架构：从"双刀互砍"到"一层一刀"

### 旧设计（导致 27GB 模型死循环的元凶）

用户请求到达后，同时受两个超时控制：

- `RequestTimeoutInMinutes`（全局设置，默认 10 分钟）
- `HealthCheckTimeout`（虚拟模型，默认 40 秒）

两把刀同时倒计时，谁先到谁赢。管理员可能改了一个，另一个没改，以为修好了，实际上还是 5 分钟掐断。**这就是 27GB Qwen 模型永远加载不完的根因。**

### 新设计：4 层互不干涉

| 层 | 在哪配置 | 默认值 | 控制什么 |
|---|---------|--------|---------|
| **数据面** | 虚拟模型 → Request Timeout (s) | 600s | 等后端开始回答的最长时间 |
| **控制面** | 提供商 → Health Check Timeout (s) | 60s | 后台探活 `/api/tags` 等多久 |
| **预热面** | 提供商 → Warmup → 每个模型的 Timeout | 1800s | 预热加载等多久 |
| **UI 面** | 硬编码 | 3s | 管理页面快速探活 |

**一刀一个坑，谁也不再砍谁。**

---

## 二、Bug 修复

1. **Test Connection 成功后不解封** — 管理员点了测试，绿灯，但路由仍拒绝流量。现在成功后自动清除熔断器 Ban + 恢复 DB 健康标记。
2. **并发排队时超时计时器在走** — 注释写着"排队不扣超时"，但 `CancelAfter` 在 acquire 之前就启动了。修复后计时器只在拿到并发槽后才启动。
3. **ModelWarmup 多模型复用 Client 崩溃** — 第二个模型的 Timeout 赋值抛 `InvalidOperationException`，预热任务连续失败。已修复。
4. **KeepAlive 帮助文本炸了 Create/Edit 页面** — `{number}{unit}` 被 Localizer 当格式化占位符，页面 500。

---

## 三、新功能 & UI

### Architecture Guide（架构指南）
新增 `/Dashboard/Guide` 页面，8 张卡片可视化完整请求流：

客户端 → 认证 & 限流 → 虚拟模型选择 → 并发排队 → 超时启动 → 实体模型 → 日志管线

每张卡片使用真实数据（从默认模型和提供商读出），管理员一看就懂。

### 状态指南
VirtualModels 列表页右侧新增 Sidebar，解释 Ready / Healthy / Down / Banned 四种状态的含义、是否接受请求、是否已加载。

### 其他 UI 优化
- `RequestTimeoutSeconds` 增加 tooltip：建议值（小模型 60s / 大模型 600s / Embedding 30s）、不计排队、流式不受限
- `MaxParallelism` 增加帮助文本：排队不消耗虚拟模型超时
- `KeepAlive` 增加格式警告：用 `5m` 不要写 `300`
- Provider 列表新增两列：并发状态（活跃/最大 + 排队数）和健康检查超时
- Chat 按钮改为直达 ChatPlayground 并自动预填模型名

---

## 四、可观测性

- `ProviderConcurrencyLimiter` 新增排队人数计数器（原子操作，零热路径开销）
- Guide 和 Provider 列表可视化：🏠 里面显示 Processing，外面排队 Waiting

---

## 五、服务器诊断发现

在 proart 服务器上确认了几个问题：

1. **Warmup 失效**：`keep_alive=1m`，预热每 5 分钟跑一次，模型只活 1 分钟，80% 时间冷着。已改为 5m。
2. **Moog（DGX Spark）利用率低**：vLLM `max-num-seqs=64`，但 Gateway `MaxParallelism=4`，差了 16 倍。
3. **两个虚拟模型超时仍是旧值 40s**：`aiursoft-moog:latest` 和 `aiursoft-super:latest` 需要手动更新。
4. **Gateway 部署时 Ollama 断开**：不是 Ollama 崩溃，是容器重启导致连接断开，keep_alive 到期后模型正常卸载。

---

## 改动量

- **35 个文件**（含 2 个 EF 迁移、1 个新页面、1 个新 ViewModel）
- **279 个测试全部通过**
- 5 次提交推送到 master

Anduin
