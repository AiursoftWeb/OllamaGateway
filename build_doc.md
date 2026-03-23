# 🚀 Virtual Model Multi-Backend & HA 重构计划书 (build_doc.md)

## 1. 概述 (Overview)
本计划旨在将 `OllamaGateway` 的模型映射逻辑从现有的 **1:1 (Virtual Model -> Single Provider)** 升级为 **1:N (Virtual Model -> Multiple Backends)**。
通过此次重构，系统将支持：
*   **负载均衡 (Load Balancing)**：在多个健康的后端之间分配请求。
*   **自动容错 (Automatic Fallback)**：当主节点不可用时，自动切换到备用节点。
*   **健康监控 (Health Monitoring)**：实时感知后端模型状态。

## 2. 数据模型变更 (Data Schema)

### 2.1 新增实体：`VirtualModelBackend`
用于描述虚拟模型与具体物理模型提供商之间的连接。
*   `Id`: 主键。
*   `VirtualModelId`: 外键，关联虚拟模型。
*   `ProviderId`: 外键，关联 Ollama 提供商。
*   `UnderlyingModelName`: 字符串。在该提供商上的真实模型名称。
*   `Priority`: 整数。用于 Fallback 模式（数字越小优先级越高）。
*   `Weight`: 整数。用于加权负载均衡（如 1, 2, 5）。
*   `Enabled`: 布尔值。手动开关。
*   `IsHealthy`: 布尔值。由监控服务维护。
*   `LastCheckedAt`: 时间戳。上次健康检查时间。
### 2.2 重构实体：`VirtualModel`
*   **废弃字段**：`ProviderId`, `UnderlyingModel`, `KeepAlive`（需编写迁移脚本将数据平滑迁移）。
*   **新增配置字段**：
    *   `SelectionStrategy`: 枚举 (`PriorityFallback`, `WeightedRandom`, `RoundRobin`)。
    *   `MaxRetries`: 整数。自动失败重试的最大次数。
    *   `HealthCheckTimeout`: 整数。单次健康检查的超时秒数。
    *   `MaxContext`: 整数。该虚拟模型支持的最大上下文长度。

### 2.3 物理提供商变更：`OllamaProvider`
*   **属性迁移**：`KeepAlive` (string/int) 应该设置在物理提供商或具体的 `VirtualModelBackend` 上。考虑到不同服务器的显存压力不同，建议在 `OllamaProvider` 设置默认保活策略，在 `VirtualModelBackend` 设置特定覆盖。

## 3. 核心逻辑实现 (Core Logic)

### 3.1 运行模式深度区分 (Mode Differentiation)
*   **负载均衡模式 (Load Balancing - Weighted/RoundRobin)**：
    *   **核心逻辑**：即使“节点 A”完全正常，系统也会根据权重或轮询顺序，主动将部分请求发送到“节点 B”。
    *   **目的**：分担并发压力，避免单点过载。
*   **自动容错模式 (Fallback - Priority)**：
    *   **核心逻辑**：只要“高优先级节点（如 Priority 0）”正常存活，系统**永远不会**主动给低优先级节点发请求。
    *   **目的**：节省昂贵/远程资源，仅在主节点宕机时作为“救命稻草”。

## 8. 可视化状态机逻辑 (Visualization & Health Logic)

为了让前端仪表盘能直观反映模型状态，定义以下分级健康体系：

### 8.1 虚拟模型 (Virtual Model) 总状态计算
虚拟状态采用“向上兼容”原则：
*   **🟢 Ready (绿色)**：**只要有一个**关联的 Backend 处于 `Ready` 状态。
    *   *含义*：用户请求可以立即得到响应，无需等待模型加载。
*   **🔵 Healthy (蓝色)**：没有 Backend 处于 `Ready`，但**至少有一个** Backend 处于 `Healthy`。
    *   *含义*：后端服务在线，但模型目前未载入显存。首个请求会触发加载（冷启动），会有明显延迟。
*   **🔴 Down (红色)**：所有关联的 Backend 全部处于 `Down` 状态。
    *   *含义*：该虚拟模型完全不可用。

### 8.2 后端节点 (Backend) 状态判定
*   **Ready (就绪)**：
    *   `GET /api/ps` 的返回列表中包含该模型。
    *   *表现*：模型已驻留 VRAM。
*   **Healthy (健康)**：
    *   `GET /api/tags` 能查到模型，且基础网络通信正常，但 `api/ps` 中不含该模型。
    *   *表现*：服务在线，但模型已从显存卸载或从未加载。
*   **Down (宕机)**：
    *   网络超时、5xx 错误、认证失败或物理提供商上找不到对应的 `UnderlyingModelName`。

---

## 9. FAQ / 开发者指南
    *   **仅内存存储**。不考虑分布式状态同步，不考虑 Scale-out。
    *   对于 `RoundRobin`，在内存中维护一个 `ConcurrentDictionary<int, int>` 记录每个 `VirtualModelId` 上次使用的索引。
*   **过滤逻辑**：
    1. 剔除 `Enabled == false` 的后端。
    2. 剔除 `IsHealthy == false` 的后端。
    3. (可选) 检查请求 Token 数是否超过后端承载能力。

### 3.2 健康监控服务 (`BackendHealthMonitor`)
*   **主动探测**：周期性（如 1 分钟）轮询。
*   **判定标准**：
    *   调用 Provider 的 `/api/ps`。
    *   **Ready 标准**：如果返回的运行列表中包含 `UnderlyingModelName`，则认为该后端处于“热就绪”状态。
    *   如果模型未加载但服务在线，可根据配置决定是否标记为 Healthy（考虑加载模型的冷启动时间）。

### 3.3 请求转发与重试 (`Controller Logic`)
*   **请求缓冲**：在控制器入口调用 `HttpContext.Request.EnableBuffering()`。
*   **重试工厂**：由于 `HttpRequestMessage` 无法重复发送，必须为每次重试创建新的实例。
*   **Fallback 边界**：
    *   **建立连接前**：如果 HttpClient 抛出异常或返回 5xx，触发 Fallback。
    *   **开始传输后**：一旦 `Response.HasStarted` 为 `true`（即已向客户端发送了第一个字节），禁止 Fallback，直接报错中断。这适用于 Streaming 和非 Streaming 请求。
    *   **预期管理**：需要明确，Fallback 主要是防范“网络不通”和“连接被拒”。在 LLM 场景下，如果实体模型已开始返回（如响应 200 OK 并开启流）但生成中途崩溃，此时由于 `Response.HasStarted` 已为 `true`，无法保活或 Fallback，用户端将看到流突然截断。

## 4. 界面设计 (UI/UX)
*   **Master-Detail 模式**：
    *   允许先创建一个“空壳”虚拟模型（仅设置名称、策略、参数等）。
    *   在编辑页面下方提供一个子表格，用于添加/删除后端提供商。
*   **防呆校验**：
    *   添加后端时，强校验后端模型类型（Chat/Embedding）必须与虚拟模型一致。

## 5. 技术实施细则 (Engineering Deep Dive)

### 5.1 认证与上下文透传
*   **Provider 隔离**：每个后端节点（Backend）都绑定在一个特定的 Provider 上。重构时必须确保从 `IModelSelector` 获取结果后，正确提取该 Provider 的 `BaseUrl` 和 `BearerToken`。
*   **HttpClient 生命周期**：在 `Controller` 中使用 `IHttpClientFactory` 创建客户端。严禁复用 `HttpRequestMessage` 实例，每次重试必须通过工厂重新构建 Body 内容。

### 5.2 审计记录与使用追踪
*   **RequestLog 扩展**：在 `RequestLog` 实体中增加 `BackendId` (Nullable int)。
*   **使用量统计**：更新 `MemoryUsageTracker`，确保统计的是最终成功的那个 Backend 所在的 Provider 资源。

### 5.3 数据库平滑迁移方案
*   **保留兼容性**：在 `VirtualModel` 实体中，不要立即物理删除 `ProviderId` 字段。
*   **迁移脚本逻辑**：
    1.  创建新表 `VirtualModelBackends`。
    2.  扫描 `VirtualModels`，对每一行，创建一个对应的 `VirtualModelBackend` 记录，其中 `Priority=0`, `Weight=1`。
    3.  代码中将旧字段标记为 `[Obsolete]`。

### 5.4 超时与熔断细节
*   **多级超时控制 (Timeout Strategy)**：
    *   **单次首 Token 超时 (Per-Try Timeout)**：每个虚拟模型和实体模型层面需引入单次尝试超时（从请求到来到开始产出首个 Token 的耗时）。若主节点在此时间内因网络黑洞等原因未响应，需及时斩断并重试备用节点，防止后续重试被耗尽时间。
    *   **全局执行超时 (Global Timeout)**：项目已有的全局超时（如 10 分钟），涵盖了从请求到达直至所有 Token 生成结束的完整生命周期。若超过此时长仍未处理完，则无论实体模型状态如何都要强制终止请求。
*   **被动熔断与动态恢复**：如果一个 Backend 连续返回 3 次非 4xx 错误，逻辑层应在内存中将其标记为“临时不可用” 5 分钟。为了避免生硬阻断，此机制与 1 分钟的“主动健康探测”结合，如果主动探测发现节点已提前恢复（例如 `/api/ps` 调用正常），应立即解除这 5 分钟的被动熔断惩罚。

## 6. 测试策略 (Testing Strategy)
*   **回归测试**：所有 `tests/IntegrationTests/` 下的现有测试必须通过。
*   **新增场景**：
    *   *Scenario A*: 两个后端，后端 A 离线，验证系统是否自动切换到后端 B 且响应正确。
    *   *Scenario B*: 两个后端，后端 A 响应慢（触发超时），验证系统是否 Fallback 到 B。
    *   *Scenario C*: 无后端可用，验证返回 503 状态码。

## 7. FAQ / 开发者指南

**Q: 系统支持多实例部署吗？**
A: 不支持。目前设计方案基于单机内存状态，不支持横向扩展 (Scale-out)。

**Q: 发生 Fallback 时日志怎么记？**
A: `RequestLog` 仅记录最终成功的那个 `BackendId`。中间失败的过程（如 A 超时，B 报错 500）记录在系统的 `ErrorLog` 或 `TraceLog` 中，以便调试。

**Q: 如果一个虚拟模型没有任何可用的后端怎么办？**
A: API 应返回 `503 Service Unavailable`。在 `/v1/models` 列表中仍显示该模型，确保客户端不会因模型消失而崩溃。

**Q: 超时怎么算？**
A: 全局请求超时（Global Timeout）依然生效。如果后端 A 耗尽了大部分时间，后续重试将受到全局剩余时间的限制。

**Q: 为什么不用分布式缓存记录轮询状态？**
A: 保持简单。本项目定位是高性能网关，内存操作的开销最小，且单机环境下的内存轮询足以保证负载均衡的统计均匀。

---

**文档版本**: 1.0.0  
**归档日期**: 2026-03-23  
**状态**: 计划中 (Planned)
