# MyScent P0-A 技术架构 v0.1

- 状态：Draft for Technical Review
- 对应 Issue：#1
- 对应 PR：#2
- 适用范围：P0-A 因果模型原型
- 推荐技术栈：.NET 8 + ASP.NET Core + SqlSugar + MySQL 8 + Vue 3 + TypeScript + Vite + Pinia
- 架构形态：模块化单体；计算引擎为无框架依赖纯函数库

## 1. 架构目标

P0-A 的技术架构只服务一个核心命题：

> 玩家在工作台上的每一次调配动作，都能够以可审计命令改变统一配方状态；统一配方通过固定版本的确定性引擎得到香味结果；结果再由场景层转译成可玩的反馈。

因此架构必须优先保证：

1. **计算确定性**：同输入、同规格版本得到同一结构化结果；
2. **配方因果可追溯**：每次加、减、换、移除、撤销均有命令和事件；
3. **场景与计算隔离**：实验室和魔法师不能拥有两套配方算法；
4. **版本可回放**：历史配方保存引擎、积木库和关系库版本；
5. **失败不丢数据**：分析、网络和页面失败不能破坏配方；
6. **普通与进阶共底层**：界面展示深度不同，配方记录不能分裂；
7. **先完成垂直切片**：不为未来社区、交易和生产提前拆微服务。

## 2. 架构原则

### A-01 模块化单体优先

P0-A 不使用微服务。

原因：

- 计算、配方、版本和工作台需要高频一致性联调；
- 当前团队规模与验证阶段不适合承担分布式事务、部署和可观测性成本；
- 模块边界可以先在代码和数据库访问层保持清晰，后续再按真实压力拆分。

### A-02 计算引擎零业务基础设施依赖

`MyScent.ScentEngine` 不依赖：

- ASP.NET Core；
- SqlSugar；
- MySQL；
- Redis；
- AI Provider；
- 文件上传；
- 场景素材；
- 用户身份。

输入为不可变计算请求，输出为不可变预测结果。

### A-03 配方聚合是修改入口

任何代码不得绕过 `FormulaAggregate` 直接写当前组件表。

所有修改通过：

`Command → Guard → Aggregate mutation → Event → Revision → Persistence`

### A-04 事件溯源轻量化

P0-A 不实现完整通用事件溯源框架，但采用 append-only 配方事件日志：

- 当前状态用于快速读取；
- 事件用于审计、撤销、回放和恢复；
- 版本快照用于长期稳定读取；
- 事件和当前状态在同一数据库事务内提交。

### A-05 核心结构化结果不可由 AI 生成

AI 只能读取计算结果做解释和场景措辞。

禁止 AI 决定：

- 数值；
- 规则是否命中；
- 配方 revision；
- 公式哈希；
- 材料权限；
- 安全与生产结论。

### A-06 P0-A 不做伪离线同步

P0-A 前端先支持本地持久化和单会话恢复。

云端账户同步、游客转登录和多设备冲突完整流程属于 P0-C；但命令 ID、revision 和本地命令日志必须从 P0-A 开始保留。

## 3. 总体结构

```text
┌────────────────────────────────────────────────────────────┐
│ Vue 3 Workbench                                             │
│ Scene Renderer │ Formula Store │ Analysis Panel │ IndexedDB │
└──────────────────────────────┬─────────────────────────────┘
                               │ HTTP JSON
┌──────────────────────────────▼─────────────────────────────┐
│ ASP.NET Core API                                            │
│ Command Endpoints │ Query Endpoints │ Problem Details       │
├────────────────────────────────────────────────────────────┤
│ Application                                                 │
│ FormulaCommandService │ AnalysisService │ VersionService     │
├────────────────────────────────────────────────────────────┤
│ Domain                                                      │
│ FormulaAggregate │ FormulaEvent │ FormulaVersion │ Policies  │
├─────────────────────────────┬──────────────────────────────┤
│ ScentEngine                  │ Infrastructure                │
│ Pure deterministic library   │ SqlSugar │ MySQL │ SpecLoader │
│ Blocks/relations/engine spec │ Event/Idempotency repositories│
└─────────────────────────────┴──────────────────────────────┘
```

## 4. 推荐仓库结构

```text
myscent/
├─ src/
│  ├─ backend/
│  │  ├─ MyScent.Api/
│  │  ├─ MyScent.Application/
│  │  ├─ MyScent.Domain/
│  │  ├─ MyScent.Infrastructure/
│  │  └─ MyScent.ScentEngine/
│  └─ web/
│     ├─ src/
│     │  ├─ app/
│     │  ├─ modules/workbench/
│     │  ├─ modules/material-catalog/
│     │  ├─ modules/formula-version/
│     │  ├─ scenes/laboratory/
│     │  ├─ scenes/alchemy/
│     │  └─ shared/
│     └─ package.json
├─ tests/
│  ├─ MyScent.ScentEngine.Tests/
│  ├─ MyScent.Domain.Tests/
│  ├─ MyScent.Application.Tests/
│  └─ MyScent.Api.IntegrationTests/
├─ specs/
│  ├─ scent/
│  └─ workbench/
├─ tools/
└─ docs/
```

P0-A 不建立大量“通用基础框架”项目。五个后端项目已足够表达依赖边界。

## 5. 后端项目职责

## 5.1 MyScent.ScentEngine

### 职责

- 加载并验证版本化积木、关系和引擎参数；
- 规范化组件；
- 计算有效贡献和遮盖后的可感知贡献；
- 计算 8 项属性；
- 计算前中后调、主体和家族贡献；
- 计算 clarity、complexity、continuity、balance、confidence；
- 生成结构化关系证据、问题和标准建议动作；
- 生成规范化数值输出哈希；
- 执行固定夹具。

### 公开接口

```csharp
public interface IScentCalculator
{
    ScentPrediction Calculate(ScentCalculationRequest request);
}
```

### 关键类型

- `ScentCalculationRequest`；
- `FormulaInputComponent`；
- `ScentBlockDefinition`；
- `ScentRelationDefinition`；
- `ScentEngineParameters`；
- `ScentPrediction`；
- `ScentEvidence`；
- `ScentProblem`；
- `ScentSuggestion`。

### 数值策略

- 输入份数使用 `decimal` 保存；
- `Math.Pow` 计算前将比例转换为 `double`；
- 每个标准算法节点按 `internal_precision = 8` 取整；
- API 输出按 4 位小数；
- 回归比较容差 `0.0001`；
- 规范化数值输出哈希使用排序后、4位小数字符串；
- 配方哈希只包含组件与规格版本，不包含计算结果和场景。

这样避免将平台相关浮点微小差异扩散为用户可见差异。

### 规格加载

启动时加载：

- `scent-blocks.v0.1.json`；
- `scent-relations.v0.1.json`；
- `scent-engine.v0.1.json`。

任一规格校验失败：

- API 启动失败；
- 输出明确错误；
- 不回退到隐式默认值；
- 不让 AI 临时补全。

## 5.2 MyScent.Domain

### FormulaAggregate

负责：

- 当前 revision；
- 当前组件集合；
- 当前配方状态；
- 封存状态；
- 撤销边界；
- 命令 guard；
- 领域事件生成。

聚合内部只保存相对份数和身份，不自行计算香味。

### 领域事件

首批事件：

- `FormulaCreated`；
- `ComponentAdded`；
- `ComponentIncreased`；
- `ComponentDecreased`；
- `ComponentQuantitySet`；
- `ComponentRemoved`；
- `ComponentReplaced`；
- `OperationUndone`；
- `FormulaVersionSnapshotted`；
- `FormulaSealed`；
- `FormulaBranched`。

### 撤销

撤销不是删除事件，而是：

1. 找到最近可撤销事件；
2. 计算反向变化；
3. 追加 `OperationUndone`；
4. revision + 1；
5. 重新计算公式哈希；
6. 旧事件继续保留。

## 5.3 MyScent.Application

### FormulaCommandService

流程：

```text
校验命令包
→ 查询幂等记录
→ 读取聚合与当前 revision
→ 校验 expected_revision
→ 执行领域命令
→ 在事务中写当前状态、事件和幂等结果
→ 返回 component delta 与公式哈希
```

### AnalysisService

流程：

```text
读取指定 formula revision 快照
→ 构造 ScentCalculationRequest
→ 调用纯函数引擎
→ 保存 AnalysisSnapshot
→ 若当前 revision 未变化，标记为 fresh
→ 若已经变化，保存为历史结果但标记 stale
```

P0-A 可同步执行完整分析；代码接口预留未来后台任务，但不先引入消息队列。

### VersionService

负责：

- 创建不可变快照；
- 从快照建立新配方分支；
- 保存规格版本；
- 读取版本差异；
- 校验封存边界。

## 5.4 MyScent.Infrastructure

### 技术选择

- SqlSugar：仓储与事务；
- MySQL 8：服务端持久化；
- System.Text.Json：JSON；
- 本地文件规格加载：P0-A；
- Redis：P0-A 不需要；
- 消息队列：P0-A 不需要；
- 对象存储：P0-A 不需要。

### 事务边界

一个修改命令在一个 MySQL 事务内完成：

- 更新 `formula` revision 与当前状态；
- 更新 `formula_component_current`；
- 插入 `formula_event`；
- 插入 `idempotency_record`。

任何一步失败全部回滚。

## 5.5 MyScent.Api

职责：

- 路由与 DTO；
- 身份/游客 ID 解析；
- 输入格式校验；
- 调用 Application；
- RFC 7807 Problem Details；
- correlation ID；
- 不包含香味算法和配方业务规则。

## 6. 数据库模型

## 6.1 formula

| 字段 | 类型建议 | 说明 |
|---|---|---|
| id | char(26) / varchar(36) | ULID或UUID |
| owner_id | varchar(64), nullable | P0-A游客可空 |
| guest_id | varchar(64), nullable | 本地游客标识 |
| title | varchar(128), nullable | 实验编号或名称 |
| status | varchar(32) | editing/sealed |
| revision | bigint | 乐观并发版本 |
| formula_hash | char(64) | 规范化输入哈希 |
| current_analysis_revision | bigint, nullable | 最新完整分析对应 revision |
| source_formula_version_id | varchar(36), nullable | 分支来源 |
| created_at | datetime(6) | UTC |
| updated_at | datetime(6) | UTC |

## 6.2 formula_component_current

| 字段 | 说明 |
|---|---|
| formula_id | 配方 |
| block_id | 香气积木 |
| relative_quantity | decimal(18,6) |
| stage_role_override | nullable |
| added_order | int |
| source_type | builtin/external |

联合唯一键：`formula_id + block_id`。

## 6.3 formula_event

| 字段 | 说明 |
|---|---|
| event_id | 唯一事件 |
| formula_id | 聚合 |
| revision | 此事件后的 revision |
| event_type | 类型 |
| command_id | 来源命令 |
| actor_id | 操作者 |
| before_state_hash | 前状态 |
| after_state_hash | 后状态 |
| payload_json | 事件载荷 |
| engine_versions_json | 规格版本 |
| created_at | 服务端时间 |

唯一键：`formula_id + revision`、`command_id`。

## 6.4 idempotency_record

| 字段 | 说明 |
|---|---|
| actor_scope | 用户或游客作用域 |
| idempotency_key | 幂等键 |
| request_hash | 防止同键不同载荷 |
| response_json | 原成功响应 |
| expires_at | 可清理时间 |

唯一键：`actor_scope + idempotency_key`。

## 6.5 analysis_snapshot

| 字段 | 说明 |
|---|---|
| analysis_id | 唯一分析 |
| formula_id | 配方 |
| formula_revision | 输入 revision |
| formula_hash | 输入哈希 |
| engine_version | 引擎版本 |
| block_library_version | 积木库版本 |
| relation_library_version | 关系库版本 |
| prediction_json | 完整结构化结果 |
| numeric_output_hash | 数值输出哈希 |
| confidence_score | 索引字段 |
| created_at | UTC |

唯一键建议：`formula_id + formula_revision + engine_version + block_library_version + relation_library_version`。

## 6.6 formula_version

| 字段 | 说明 |
|---|---|
| id | 版本ID |
| formula_id | 原配方 |
| version_no | 展示编号 |
| source_revision | 快照 revision |
| formula_hash | 输入哈希 |
| components_json | 不可变组件快照 |
| prediction_snapshot_id | 分析快照 |
| label | 用户标签 |
| sealed | 是否完成封存 |
| created_at | UTC |

## 6.7 spec_manifest

记录当前部署可用的：

- engine version；
- block library version；
- relation library version；
- 文件 SHA-256；
- 状态；
- 加载时间。

历史版本文件必须保留，不能原路径覆盖后让旧配方无法回放。

## 7. API 边界

P0-A 推荐统一使用命令入口，避免为每种操作造不同业务实现。

### 创建配方

`POST /api/v1/formulas`

### 执行命令

`POST /api/v1/formulas/{formulaId}/commands`

请求体使用 `formula-command-protocol.v0.1.json`。

### 获取当前配方

`GET /api/v1/formulas/{formulaId}`

### 完整分析

`POST /api/v1/formulas/{formulaId}/analyses`

必须携带 `formulaRevision`。

### 获取分析

`GET /api/v1/formulas/{formulaId}/analyses/{analysisId}`

### 获取材料目录

`GET /api/v1/scent-blocks?libraryVersion=0.1.0`

### 获取历史版本

`GET /api/v1/formulas/{formulaId}/versions`

### 读取规格版本

`GET /api/v1/spec-manifest`

场景切换 P0-A 主要是前端状态，不调用配方修改接口。

## 8. 前端架构

## 8.1 Vue 模块

### workbench

- `formulaStore`：组件、revision、公式哈希、撤销能力；
- `analysisStore`：fresh/stale/pending/failed；
- `persistenceStore`：local/sync/conflict；
- `sceneStore`：实验室/魔法师表现；
- `commandClient`：生成命令ID、幂等键和expected revision；
- `localJournal`：IndexedDB命令日志；
- `workbenchSelectors`：从结构化状态生成UI视图模型。

### material-catalog

- 24个积木目录；
- 家族筛选；
- 材料详情；
- 现实材料方向；
- 不暴露生产层字段。

### formula-version

- 快照；
- 分支；
- 差异；
- 封存。

## 8.2 前端状态原则

前端不能把容器动画当作配方状态。

```text
Formula state → View model → Scene animation
```

不能反向：

```text
Scene animation → 猜测 formula state
```

### 乐观更新

P0-A 可以对加减动作做临时视觉反馈，但：

- 命令失败必须回滚；
- revision 冲突必须停止继续提交；
- 服务端响应的公式状态是最终权威；
- 幂等重试不得重复播放“成功加入”结算。

## 8.3 IndexedDB

本地保存：

- 当前配方快照；
- 最近确认的 revision；
- 未确认命令日志；
- 当前场景；
- 最近分析快照；
- 规格版本；
- 更新时间。

不保存生产层秘密字段。

## 9. 场景适配器

统一接口示意：

```ts
interface SceneAdapter {
  sceneId: string;
  translateCommand(command: StandardCommand): SceneAction;
  translateFeedback(feedback: StandardFeedback): SceneFeedback;
  translateSuggestion(suggestion: StandardSuggestion): SceneSuggestion;
}
```

实验室：

- 移液器、分析仪、曲线、警告灯；
- 使用精密、理性语言。

魔法师：

- 药剂瓶、符号、雾气、封印；
- 使用仪式化语言。

两者不得改变：

- 标准命令；
- 组件和份数；
- 公式哈希；
- 结构化分析；
- 证据和建议目标。

## 10. 分析执行策略

### 即时分析

用途：操作后快速反馈。

输出子集：

- 变化最大的 1～3 个属性；
- 主体是否变化；
- 新增严重问题；
- 阶段份额变化。

可以同步执行同一引擎，但前端只展示子集。

### 完整数字闻香

- 绑定不可变 revision；
- 计算完整结果；
- 保存快照；
- 返回后检查当前 revision；
- 若已经变化，标记 stale，不覆盖当前状态。

P0-A 不需要队列。若后续分析加入 AI 解释或耗时模型，再拆分异步任务。

## 11. 哈希与版本

### formula_hash

包含：

- 按 block_id 排序的组件与相对份数；
- engine version；
- block library version；
- relation library version。

排除：

- scene_id；
- 显示语言；
- 用户标题；
- 解释文案；
- 海报和视频；
- 时间戳。

### numeric_output_hash

包含：

- 按固定键顺序序列化；
- API 4 位小数结果；
- 结构化阶段、属性、派生指标和规则ID。

用于：

- 100次确定性测试；
- 跨场景一致性；
- 历史回放；
- 版本差异审计。

## 12. 错误处理

API 使用 Problem Details：

```json
{
  "type": "https://myscent/errors/revision-conflict",
  "title": "配方版本冲突",
  "status": 409,
  "code": "REVISION_CONFLICT",
  "currentRevision": 12,
  "expectedRevision": 10,
  "correlationId": "..."
}
```

关键规则：

- 命令失败不部分写入；
- 分析失败不改变配方；
- 规格加载失败阻止启动；
- 旧分析返回不覆盖新revision；
- 日志不记录生产层秘密或完整私人配方载荷。

## 13. 可观测性

P0-A 最小记录：

- correlation ID；
- command ID；
- formula ID（可脱敏）；
- expected/current revision；
- engine/block/relation version；
- formula hash；
- 分析耗时；
- 命中问题代码；
- 错误代码。

禁止日志记录：

- 生产配方完整明文；
- 供应商私有信息；
- 用户故事原文（除非明确诊断授权）；
- 密钥和Token。

## 14. 测试架构

### 单元测试

`MyScent.ScentEngine.Tests`

- 读取15个夹具；
- 参数化运行；
- 属性容差0.0001；
- 结构化关系和问题精确比较；
- 100次hash一致。

`MyScent.Domain.Tests`

- revision；
- 替换原子性；
- 撤销补偿；
- 封存不可变；
- 分支继承。

### 集成测试

- 命令事务；
- 幂等重试；
- revision冲突；
- 事件与当前状态一致；
- 分析快照绑定revision；
- 历史版本回放。

### 前端测试

- stale分析标识；
- 命令失败回滚；
- 场景切换数值不变；
- 同步失败恢复；
- 数字闻香面板的结构化展示。

## 15. CI

现有 `Spec Validation` 继续检查规格完整性。

代码加入后建议新增：

1. `dotnet format --verify-no-changes`；
2. `dotnet test`；
3. 前端 typecheck；
4. 前端 unit test；
5. production build；
6. 固定夹具差异报告；
7. 依赖漏洞检查后续加入。

任何规格文件变化必须触发：

- Python结构校验；
- C#引擎回归测试；
- fixture差异审计。

## 16. P0-A 部署形态

P0-A 内测建议：

- 一个 ASP.NET Core 服务；
- 一个 Vue 静态站；
- 一个 MySQL 实例；
- Nginx 反向代理；
- 单区域部署；
- 不使用Kubernetes；
- 不拆Redis和消息队列。

Docker Compose 足够支持开发和内测。

## 17. 安全边界

- 配方权限由服务端校验；
- 客户端不接收生产层字段；
- 公开材料方向与生产映射分 DTO；
- 相对份数不宣称安全浓度；
- 任何真实材料录入和安全规则属于后续独立模块；
- 场景动画不得输出可模仿的危险化学步骤。

## 18. 后续可拆分点

只有出现真实需要才拆：

- ScentEngine：计算量或多语言SDK需求显著；
- Media：海报/视频异步任务；
- Community：信息流和互动规模增长；
- Fulfillment：生产、批次与订单；
- AI Explanation：独立成本与策略治理。

P0-A 均不拆。

## 19. 关键架构决策摘要

| 决策 | 选择 |
|---|---|
| 系统形态 | 模块化单体 |
| 后端 | .NET 8 / ASP.NET Core |
| 数据访问 | SqlSugar |
| 数据库 | MySQL 8 |
| 前端 | Vue 3 / TypeScript / Vite / Pinia |
| 计算引擎 | 无框架依赖C#纯函数库 |
| 配方修改 | 命令 + 聚合 + append-only事件 |
| 并发 | expected revision乐观并发 |
| 撤销 | 补偿事件 |
| 分析 | P0-A同步，绑定不可变revision |
| 本地恢复 | IndexedDB命令日志与快照 |
| 场景 | Adapter转译，不进入公式hash |
| 规格 | JSON版本化并启动强校验 |
| 部署 | Docker Compose单体部署 |

## 20. 进入代码实现前的验收

架构进入实现阶段前，需要确认：

1. P0-A 使用 .NET 8 + Vue 3；
2. 当前采用模块化单体；
3. MySQL + SqlSugar作为服务端持久化；
4. 配方聚合和事件日志不被绕过；
5. 场景不进入核心计算；
6. 完整分析P0-A先同步执行；
7. 规格加载失败阻止服务启动；
8. 生产层字段与创作层字段分离；
9. PR继续保持Draft，代码通过独立后续分支和PR交付更合适。

该文档未经过项目负责人验收前，不作为正式冻结架构。