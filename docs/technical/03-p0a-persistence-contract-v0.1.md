# MyScent P0-A 持久化事务契约 v0.1

- 状态：Draft for Infrastructure Review
- 对应 Issue：#1
- 对应父 PR：#2
- 对应实现 PR：#4
- 数据库目标：MySQL 8.x
- ORM 计划：SqlSugar（本阶段尚未引入包）
- 上游契约：`formula-command-protocol.v0.1.json`、`00-p0a-architecture-v0.1.md`

## 1. 目的

本契约固定 MyScent P0-A 的持久化边界，避免数据库实现把已经通过回归的领域语义改写为普通 CRUD。

核心要求：

> 一个配方修改命令，要么完整提交 revision、当前组件投影、追加事件和幂等响应，要么全部不发生。

数据库不是第二套配方规则引擎。配方是否合法、替换是否原子、撤销如何补偿，仍由 `FormulaAggregate` 决定；数据库只负责并发、事务、恢复和唯一约束。

## 2. 数据事实分层

### 2.1 当前状态投影

- `myscent_formula`
- `myscent_formula_component_current`

用于快速读取当前工作台，不是唯一审计证据。

### 2.2 追加历史

- `myscent_formula_event`

事件不可更新、不可物理删除。服务恢复时可从 `formula_created` 开始回放，并重建：

- 当前组件；
- revision；
- 封存与标题状态；
- 配方哈希；
- 聚合状态哈希；
- 可撤销栈及补偿关系。

### 2.3 请求去重

- `myscent_idempotency_record`

幂等范围为 `actor_scope + idempotency_key`。同键同语义返回原响应；同键不同请求指纹必须返回冲突。

### 2.4 不可变结果

- `myscent_analysis_snapshot`
- `myscent_formula_version`

分析绑定明确的配方 revision 和规格版本。旧 revision 的分析可以保存，但不能覆盖当前 fresh 状态。

### 2.5 规格追踪

- `myscent_spec_manifest`

历史引擎、积木和关系规格不能被原路径静默覆盖；旧事件与版本必须能按原版本回放。

## 3. 配方创建事务

创建配方必须在同一事务内：

1. 插入 `myscent_formula`，revision 为 0；
2. 插入 revision 0 的 `formula_created` 事件；
3. 写入初始配方哈希与聚合状态哈希；
4. 提交事务。

当前领域创建事件使用 `Guid.Empty` 作为内部命令标记，持久化时映射为 `NULL`。因此：

- `myscent_formula_event.command_id` 允许 NULL；
- 真实修改命令 ID 仍受唯一索引保护；
- 多个配方创建事件不会争用同一个空 GUID。

## 4. 修改命令事务

`IFormulaPersistenceStore.CommitCommandAsync` 的数据库实现必须执行如下顺序：

```text
BEGIN
→ 查询 actor_scope + idempotency_key
→ 若已有：比较 request_fingerprint 并返回原响应或冲突
→ SELECT formula ... FOR UPDATE
→ 校验数据库 revision == expected_revision
→ 条件更新 formula WHERE revision = expected_revision
→ 更新当前组件投影
→ 插入唯一 formula_event
→ 插入 idempotency_record 与完整成功响应
→ COMMIT
```

### 4.1 条件更新

推荐：

```sql
UPDATE myscent_formula
SET revision = @newRevision,
    formula_hash = @formulaHash,
    aggregate_state_hash = @aggregateStateHash,
    status = @status,
    title = @title,
    current_scene_id = @sceneId,
    updated_at = @serverTime
WHERE formula_id = @formulaId
  AND revision = @expectedRevision;
```

受影响行数必须等于 1；为 0 时回滚并返回 `REVISION_CONFLICT`。

### 4.2 当前组件

P0-A 允许事务内按完整快照重写当前组件投影，因为单个配方建议只有 3～6 个积木，最多也处于小规模范围。后续性能证据出现前，不提前增加复杂的局部合并实现。

推荐顺序：

1. 删除该公式的当前组件行；
2. 批量插入命令后的完整组件快照；
3. 保持 `formula_id + component_key` 唯一。

事件历史不会因此删除。

### 4.3 事件

每个成功修改命令只插入一个事件：

- `formula_id + revision` 唯一；
- 真实 `command_id` 唯一；
- `before_state_hash` 与锁定时当前聚合一致；
- `after_state_hash` 与提交后聚合一致；
- `component.replace` 的移除和加入存于同一个 `change_json`；
- `operation_undone` 保存 `compensates_event_id`。

### 4.4 幂等响应

`response_json` 保存首次成功返回的完整结构化结果。重试必须返回原 `event_id`、revision、哈希和服务端时间，不能重新生成一份“等价但不同”的响应。

## 5. 失败原子性

下列任一情况发生时必须回滚：

- revision 已变化；
- 幂等键与不同请求指纹冲突；
- 事件唯一约束冲突；
- 当前组件写入失败；
- 配方状态更新失败；
- 幂等记录写入失败；
- 序列化失败；
- 连接中断或事务超时。

不得出现：

- revision 已增加但没有事件；
- 事件已写入但组件仍是旧状态；
- 命令成功但没有幂等记录；
- 替换只删除旧材料、未加入新材料；
- 撤销删除历史事件。

## 6. 加载与恢复

### 6.1 正常读取

正常工作台读取可以从当前状态投影构造视图。

### 6.2 聚合执行前加载

执行命令前必须获得可执行聚合。P0-A 初始实现可：

1. 读取公式规格版本；
2. 读取全部事件并按 revision 排序；
3. 调用 `FormulaAggregate.Replay`；
4. 对比回放结果与当前状态投影；
5. 不一致时拒绝修改并记录完整性异常。

后续事件量增长后，可以引入可信快照 + 后续事件回放，但不能跳过哈希校验。

### 6.3 恢复一致性

必须比较：

- revision；
- formula hash；
- aggregate state hash；
- 当前组件；
- sealed/title 状态。

回放后还必须保留撤销能力；仅恢复最终组件但丢失撤销栈不视为合格恢复。

## 7. 分析快照事务

分析使用锁外不可变输入快照，计算完成后：

1. 插入 `myscent_analysis_snapshot`；
2. 读取或条件更新当前公式 revision；
3. 若仍等于输入 revision，标记 fresh，并更新 `current_analysis_id/current_analysis_revision`；
4. 若已变化，保存为 stale，不覆盖公式的当前分析指针。

同一个公式 revision + 同一组三个规格版本只允许一份规范化分析快照。重复请求返回已有结果。

## 8. 身份范围

P0-A 数据库保留：

- `actor_scope`：幂等和数据隔离的稳定范围；
- `owner_id`：登录用户；
- `guest_id`：游客。

每个配方必须恰好属于 owner 或 guest 之一。游客转登录属于 P0-C，迁移时必须在事务内更新所有权，不能更改配方、事件和分析哈希。

## 9. ORM 边界

SqlSugar 只允许出现在基础设施项目。以下项目不得依赖 SqlSugar：

- `MyScent.ScentEngine`；
- `MyScent.Formulas` 领域与应用契约；
- 前端场景模块。

ORM 实体不直接作为：

- API DTO；
- 领域聚合；
- 事件公开模型；
- 香味计算输入。

基础设施负责显式映射。

## 10. 初版实施顺序

1. 审查并执行 `0001_p0a_core.sql`；
2. 建立 `MyScent.Infrastructure`；
3. 增加 SqlSugar 与 MySQL 连接配置；
4. 实现事件、当前状态和幂等映射；
5. 实现创建事务；
6. 实现命令提交事务与 revision CAS；
7. 实现事件回放加载；
8. 实现分析快照保存；
9. 增加 MySQL 容器集成测试；
10. 将 API 从内存服务切换为持久化服务。

切换前必须保留内存实现作为领域测试替身，不删除已通过的回归测试。

## 11. 当前完成定义

本持久化基线达到可进入 ORM 实现状态，需要：

- 数据库迁移包含当前投影、事件、幂等、分析、版本和规格表；
- 创建事件空命令 ID 约束已处理；
- 聚合回放可重建撤销栈；
- 封存改变聚合状态哈希但不改变配方哈希；
- 仓储事务端口已固定；
- CI 校验关键表和约束；
- 最新 .NET 回归全部通过。

本基线仍不代表生产数据库已经完成安全、备份、性能与灾备审查。
