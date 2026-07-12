# MyScent

> 让调香像游戏一样可玩，让游戏中的每一步又真实影响香水结果。

MyScent 是一个把制作场景、调配仪式、香味计算和现实调香逻辑连接起来的可玩数字调香台。

用户在场景中执行的取料、滴加、混合、静置、数字试闻和修正，都会进入统一配方状态；系统计算出的前中后调、属性、主体、遮盖和冲突，又会通过场景内的视觉、声音和仪器反馈返回玩家。

故事、香语、海报、短视频、社区和交易属于制作完成后的衍生层，不是核心制作体验的前置条件。

## 产品核心

- **制作场景与调配仪式**：用精密实验室、魔法师、东方香房等交互语言承载真实调配动作。
- **统一配方与香味计算**：根据香气积木、相对比例、阶段倾向、强度和关系规则产生确定性数字预测。
- **场景反馈**：把属性变化、主体被遮盖、结构失衡和修正方向变成可见、可听、可操作的反馈。
- **玩家成长**：普通、进阶和未来高级模式共用同一配方底层，逐步揭示真实材料与专业参数。
- **现实桥接**：公开材料真实性，保护复现精度；允许学习与外购，同时保留计算、版本、批次和履约价值。

## 当前阶段

项目处于 **MVP P0-A 因果模型原型准备阶段**。

当前优先验证：

1. 玩家动作是否稳定改变香味计算；
2. 计算结果是否可解释、非随机；
3. 普通玩家是否能完成加入、分析和修正；
4. 有经验玩家是否认可底层因果；
5. 同一配方在不同制作场景中是否保持核心结果一致。

在P0-A通过前，不优先建设完整社区、创作者商业化、真实履约和高成本视频。

## 材料透明原则

> 公开材料真实性，保护复现精度。

- 普通玩家看到气味意象，并可展开真实材料方向；
- 进阶玩家看到代表性材料、相对作用、阶段位置和比例；
- 高级玩家后续按权限查看精确材料、浓度、比例和导出；
- 供应商、批次、生产参数、熟化和打样修正默认不公开；
- 用户可以外购材料并继续使用MyScent的计算、记录和版本能力。

## 当前文档与规格

- `docs/product/00-product-vision.md`：产品愿景与定位；
- `docs/product/01-product-roadmap.md`：产品路线图；
- `docs/product/02-mvp-prd-v0.1.md`：已废止旧稿；
- `docs/product/02-mvp-prd-v0.2.md`：新版可玩调香MVP PRD；
- `docs/product/03-mvp-review-v0.1.md`：MVP产品审查；
- `docs/product/04-scent-blocks-v0.1.md`：24个香气积木语义规格；
- `docs/product/05-gameplay-reality-bridge-v0.1.md`：游戏与现实调香桥接框架；
- `docs/product/06-material-transparency-and-fulfillment-v0.1.md`：材料透明和履约策略；
- `docs/product/07-mvp-prd-v0.2-review-v0.1.md`：新版PRD硬审与P0-A/B/C关口；
- `docs/product/08-scent-calculation-model-v0.1.md`：香味计算规则模型；
- `specs/scent/scent-blocks.v0.1.json`：24个积木计算参数；
- `specs/scent/scent-relations.v0.1.json`：互补、桥接、遮盖、冲突与组过载规则。

## 协作方式

项目采用：

`Issue → Branch → Design/Development → PR → CI → Audit → Acceptance → Merge → Remote Readback`

- AI可以负责分析、文档、实现、测试和审计材料；
- 影响正式范围、版本、规则或发布状态的动作需项目负责人确认；
- 默认通过独立分支和Draft PR交付，不直接合并正式版本。

## 当前工作项

- [Issue #1：启动 MyScent 产品设计与 MVP 定义](https://github.com/kyao-kk/myscent/issues/1)
- [Draft PR #2：可玩数字调香与材料透明基线](https://github.com/kyao-kk/myscent/pull/2)