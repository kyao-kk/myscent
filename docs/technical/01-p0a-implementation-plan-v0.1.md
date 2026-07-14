# MyScent P0-A 实施计划 v0.1

- 状态：Draft for Review
- 对应 Issue：#1
- 对应 PR：#2
- 上游：`00-p0a-architecture-v0.1.md`
- 实施目标：通过 Gate A1、A2、A3，证明可玩调香的因果核心成立

## 1. 总体策略

P0-A 不按“先把后端全部写完，再把前端全部写完”的方式推进。

采用纵向、可验证的开发顺序：

```text
规格加载
→ 纯计算引擎
→ 固定夹具
→ 配方聚合与命令
→ API
→ 低保真工作台
→ 完整闭环
```

每个批次必须拥有可执行验收出口，未通过不得提前堆叠高完成度场景。

## 2. 批次总览

| 批次 | 名称 | 核心出口 |
|---|---|---|
| B0 | 工程骨架与规格校验 | 后端、前端、测试项目可构建 |
| B1 | 纯计算引擎 | T-001～T-008、T-013、T-014通过 |
| B2 | 关系、问题与建议 | 证据链和标准建议动作完整 |
| B3 | 配方聚合与命令 | revision、幂等、替换、撤销通过 |
| B4 | 持久化与版本 | 事件回放、快照、分支、封存通过 |
| B5 | API与客户端契约 | 命令、分析、查询可联调 |
| B6 | 低保真实验室 | 用户完成加入—分析—修正—保存 |
| B7 | P0-A审计 | CI、测试、可用性、参数评审材料齐全 |

## 3. B0：工程骨架与规格校验

### 后端

建立：

- `MyScent.Api`；
- `MyScent.Application`；
- `MyScent.Domain`；
- `MyScent.Infrastructure`；
- `MyScent.ScentEngine`。

### 测试

建立：

- `MyScent.ScentEngine.Tests`；
- `MyScent.Domain.Tests`；
- `MyScent.Application.Tests`；
- `MyScent.Api.IntegrationTests`。

### 前端

建立 Vue 3 + TypeScript + Vite + Pinia 工程。

### 规格

- 将 `specs/` 复制到构建输出或通过仓库根路径读取；
- C# 实现规格 DTO 和验证器；
- 校验 block ID、向量、阶段权重、关系引用、版本依赖；
- Python校验与C#校验必须覆盖相同核心约束。

### 验收

- `dotnet build`成功；
- `dotnet test`可运行空测试集；
- 前端 typecheck 和 build 成功；
- 三份计算规格加载成功；
- 人为破坏阶段权重时服务和测试明确失败。

## 4. B1：纯计算引擎

### 实现顺序

1. 输入规范化；
2. 相对比例；
3. 有效强度；
4. 基础属性；
5. 前中后调；
6. 主体与家族贡献；
7. 复杂度；
8. 连续性；
9. 清晰度；
10. 平衡度；
11. 置信度；
12. 规范化输出与哈希。

### 约束

- 一个公开 `Calculate` 入口；
- 无数据库和HTTP依赖；
- 输入对象不可变；
- 计算过程中不修改规格对象；
- 不读取系统时间；
- 不使用随机数；
- 不调用AI；
- 所有排序显式指定稳定键。

### 验收

- T-001单材料；
- T-005中段缺失基础结构；
- T-006尾段不足；
- T-007复杂但清晰；
- T-008简单但模糊；
- T-013运行100次hash一致；
- T-014无NaN/Infinity；
- 属性误差不超过0.0001。

## 5. B2：关系、问题与建议

### 关系执行

固定顺序：

1. 遮盖；
2. 可感知贡献重新归一化；
3. 互补；
4. 桥接；
5. 冲突；
6. 组规则。

### 证据

每个关系输出：

- rule ID；
- source；
- target；
- relation type；
- effect；
- affected metrics；
- 前后值。

### 问题

至少实现：

- sweet-heavy overload；
- too heavy；
- sharp opening；
- masked subject；
- middle missing；
- tail insufficient；
- too many themes；
- cold/warm tension；
- pair conflict；
- clean/dark/atmosphere group problems。

### 建议

输出标准动作，不输出自动修改：

- decrease component；
- increase component；
- add candidate family/block；
- replace component；
- branch before experiment。

### 验收

- T-002互补；
- T-003遮盖；
- T-004甜厚过载；
- 每个问题有证据；
- 每个阻断级问题至少有1个可执行建议；
- 建议目标引用有效block ID或明确候选集合。

## 6. B3：配方聚合与命令

### 聚合

实现：

- Create；
- Add；
- Increase；
- Decrease；
- SetQuantity；
- Remove；
- Replace；
- Undo；
- Seal；
- BranchFromVersion。

### 命令处理

- 校验command envelope；
- expected revision；
- idempotency key；
- request hash；
- 领域guard；
- 一个命令一个revision；
- 一个成功修改命令一个领域事件。

### 撤销

P0-A仅撤销最近一个未跨越封存/快照边界的可撤销修改事件。

### 验收

- 相同幂等键重试不重复加料；
- 相同幂等键不同载荷失败；
- revision错误返回冲突；
- Replace只增加1个revision；
- Replace不会短暂出现“两个材料同时存在”的持久状态；
- Undo恢复配方hash；
- T-009、T-010通过。

## 7. B4：持久化与版本

### 数据库

建立：

- formula；
- formula_component_current；
- formula_event；
- idempotency_record；
- analysis_snapshot；
- formula_version；
- spec_manifest。

### 仓储

- FormulaRepository；
- FormulaEventRepository；
- IdempotencyRepository；
- AnalysisSnapshotRepository；
- FormulaVersionRepository。

### 事务

修改命令在一个事务中完成：

- 当前状态；
- revision；
- 事件；
- 幂等结果。

### 回放

从FormulaCreated开始回放事件，必须得到：

- 相同组件；
- 相同份数；
- 相同revision；
- 相同formula hash。

### 验收

- 事务中途失败不产生半条事件；
- 当前表与事件回放一致；
- 版本快照不可变；
- 封存后直接修改被拒绝；
- 分支继承指定版本而不是当前浮动状态；
- T-015历史版本回放通过。

## 8. B5：API与契约

### 命令API

- 创建公式；
- 执行命令；
- 查询当前状态。

### 分析API

- 请求即时分析；
- 请求完整分析；
- 查询分析快照。

### 版本API

- 保存快照；
- 查询版本；
- 创建分支；
- 封存。

### 目录API

- 读取积木目录；
- 读取现实材料方向；
- 读取spec manifest。

### 错误

统一Problem Details和稳定错误码。

### 验收

- API集成测试覆盖成功和冲突；
- 无权限DTO不返回生产层字段；
- 旧revision分析返回后标记stale；
- correlation ID进入日志和错误响应。

## 9. B6：低保真实验室工作台

### 页面

P0-A用一个工作台路由承载：

- 当前容器；
- 材料架；
- 当前配方；
- 即时反馈；
- 数字闻香抽屉；
- 版本节点；
- 本地保存状态。

### 视觉

只使用：

- CSS；
- SVG；
- 轻量过渡；
- 简单液体/仪表表现。

不做3D和高精度液体模拟。

### 状态

严格对应：

- editing clean/dirty；
- analysis stale/pending/fresh/failed；
- local/sync状态；
- sealed。

### 验收任务

普通测试用户不看说明完成：

1. 加入3个积木；
2. 修改份数；
3. 数字闻香；
4. 找到一个问题；
5. 按一个建议亲自修正；
6. 重新分析；
7. 保存版本；
8. 封存或继续分支。

## 10. B7：P0-A审计

### 自动测试

- 规格校验；
- 引擎夹具；
- 领域测试；
- API集成；
- 前端单元；
- 前端构建。

### 手工审计

- 场景切换不影响数值；
- 旧分析不冒充当前；
- 分析失败不丢配方；
- revision冲突不静默合并；
- 生产层字段未泄漏；
- 预测声明始终可见。

### 用户测试

至少：

- 3名普通用户；
- 1名有经验香水用户。

P0-A只用于发现重大方向问题，不以统计显著性为目标。

## 11. 建议提交拆分

代码开发建议新建独立分支，不继续把大量代码堆入当前产品文档PR。

建议分支：

`agent/p0a-engine-foundation`

建议后续PR提交顺序：

1. `chore: scaffold P0-A backend and web projects`；
2. `feat(engine): load and validate scent specifications`；
3. `feat(engine): implement deterministic core calculation`；
4. `test(engine): execute fixed scent fixtures`；
5. `feat(domain): add formula aggregate and events`；
6. `feat(app): add idempotent formula command handling`；
7. `feat(api): expose formula and analysis endpoints`；
8. `feat(web): add low-fi laboratory workbench`；
9. `test: complete P0-A integration and recovery cases`。

## 12. 阻断级规则

出现以下情况必须停止扩展功能：

- 同输入产生不同数值；
- 场景改变核心结果；
- 命令重试重复加料；
- revision冲突自动改比例；
- 撤销无法恢复公式hash；
- 旧分析显示为当前；
- 分析失败导致配方丢失；
- AI文案反向改写结构化结果；
- 生产层字段进入普通客户端；
- 固定夹具被修改但未新增版本。

## 13. 完成定义

P0-A实施完成必须同时满足：

- Gate A1、A2、A3全部通过；
- CI全绿；
- 15个夹具全通过；
- 工作台完成闭环；
- 参数争议有记录；
- 产品、技术和有经验用户完成评审；
- 项目负责人明确验收。

未满足时不得进入P0-B高完成度场景制作。