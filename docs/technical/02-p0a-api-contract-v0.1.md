# MyScent P0-A API 契约 v0.1

- 状态：Draft for Review
- 对应 Issue：#1
- 对应 PR：#2
- 基础路径：`/api/v1`
- 数据格式：UTF-8 JSON
- 时间：ISO-8601 UTC
- 错误格式：RFC 7807 Problem Details + 稳定 `code`

## 1. 契约原则

1. 配方修改统一通过命令接口；
2. 每个修改命令必须带 `expectedRevision` 和 `idempotencyKey`；
3. 场景 ID 只影响表现，不影响配方 hash；
4. API 不返回未授权生产层字段；
5. 分析绑定明确 revision；
6. 旧 revision 分析可以保存，但不能标记为当前；
7. 数值为数字香气预测，不表示真实生产百分比或安全浓度。

## 2. 通用响应字段

所有成功响应建议包含：

```json
{
  "correlationId": "01J...",
  "serverTime": "2026-07-14T08:00:00Z"
}
```

修改响应额外包含：

```json
{
  "commandId": "...",
  "formulaId": "...",
  "previousRevision": 3,
  "newRevision": 4,
  "formulaHash": "sha256...",
  "eventId": "...",
  "eventType": "component_added",
  "undoAvailable": true
}
```

## 3. 创建配方

### `POST /api/v1/formulas`

请求：

```json
{
  "commandId": "uuid",
  "idempotencyKey": "uuid",
  "guestId": "guest-ulid",
  "sceneId": "SCENE-LAB-001",
  "experienceMode": "ordinary",
  "engineVersion": "0.1.0",
  "blockLibraryVersion": "0.1.0",
  "relationLibraryVersion": "0.1.0"
}
```

响应 `201`：

```json
{
  "formulaId": "01J...",
  "revision": 0,
  "formulaHash": "...",
  "status": "editing",
  "components": [],
  "analysisState": "not_available",
  "persistenceState": "synced"
}
```

## 4. 执行配方命令

### `POST /api/v1/formulas/{formulaId}/commands`

请求示例：

```json
{
  "commandId": "uuid",
  "idempotencyKey": "uuid",
  "commandType": "component.add",
  "expectedRevision": 0,
  "actorId": "guest-ulid",
  "sessionId": "session-ulid",
  "sceneId": "SCENE-LAB-001",
  "issuedAt": "2026-07-14T08:00:00Z",
  "payload": {
    "blockId": "SB-CA-01",
    "quantity": 4.0
  }
}
```

响应 `200`：

```json
{
  "commandId": "uuid",
  "formulaId": "01J...",
  "previousRevision": 0,
  "newRevision": 1,
  "eventId": "01J...",
  "eventType": "component_added",
  "formulaHash": "...",
  "componentDelta": {
    "added": [{"blockId": "SB-CA-01", "quantity": 4.0}],
    "updated": [],
    "removed": []
  },
  "analysisState": "stale",
  "instantFeedback": {
    "changedAttributes": [
      {"key": "freshness", "direction": "up", "magnitude": "high"}
    ],
    "primaryChanged": true,
    "newProblemCodes": ["tail_insufficient"]
  },
  "undoAvailable": true
}
```

支持命令：

- `component.add`；
- `component.increase`；
- `component.decrease`；
- `component.set_quantity`；
- `component.remove`；
- `component.replace`；
- `operation.undo`；
- `version.snapshot`；
- `version.branch`；
- `formula.seal`。

命令字段以 `formula-command-protocol.v0.1.json` 为准。

## 5. 查询当前配方

### `GET /api/v1/formulas/{formulaId}`

响应：

```json
{
  "formulaId": "01J...",
  "revision": 7,
  "formulaHash": "...",
  "status": "editing",
  "title": "实验 A014",
  "experienceMode": "ordinary",
  "versions": {
    "engine": "0.1.0",
    "blocks": "0.1.0",
    "relations": "0.1.0"
  },
  "components": [
    {
      "blockId": "SB-CA-01",
      "displayName": "佛手柑亮光",
      "relativeQuantity": 4.0,
      "normalizedShare": 0.4,
      "role": "primary",
      "sourceType": "builtin"
    }
  ],
  "latestAnalysis": {
    "analysisId": "01J...",
    "formulaRevision": 6,
    "state": "stale"
  },
  "undoAvailable": true,
  "sealed": false
}
```

普通模式可不返回内部规则强度，但结构化份数与状态不能依赖文案解析。

## 6. 请求完整分析

### `POST /api/v1/formulas/{formulaId}/analyses`

请求：

```json
{
  "analysisIdempotencyKey": "uuid",
  "formulaRevision": 7,
  "mode": "full",
  "sceneId": "SCENE-LAB-001"
}
```

响应 `200`：

```json
{
  "analysisId": "01J...",
  "formulaId": "01J...",
  "formulaRevision": 7,
  "currentFormulaRevision": 7,
  "state": "fresh",
  "formulaHash": "...",
  "numericOutputHash": "...",
  "engineVersion": "0.1.0",
  "blockLibraryVersion": "0.1.0",
  "relationLibraryVersion": "0.1.0",
  "normalizedComponents": [],
  "stages": {
    "top": {},
    "heart": {},
    "base": {}
  },
  "primaryComponents": [],
  "familyContributions": [],
  "attributes": {
    "sweetness": 0.2887,
    "freshness": 0.8167,
    "warmth": 0.4001,
    "weight": 0.2287,
    "softness": 0.6261,
    "transparency": 0.897,
    "diffusion": 0.6399,
    "longevity": 0.3459
  },
  "metrics": {
    "clarity": 0.6318,
    "complexity": 0.8116,
    "continuity": 0.5753,
    "balance": 0.6159
  },
  "relations": [],
  "problems": [],
  "suggestions": [],
  "confidence": {
    "score": 0.8927,
    "level": "high",
    "reasons": []
  },
  "predictionDisclaimerVersion": "0.1"
}
```

若请求 revision 为7，但完成时配方已变为8：

- 保存 revision 7 的分析快照；
- 返回 `state = stale`；
- 返回 `currentFormulaRevision = 8`；
- 客户端不得覆盖 revision 8 的当前即时状态。

## 7. 获取分析快照

### `GET /api/v1/formulas/{formulaId}/analyses/{analysisId}`

返回保存时的不可变结构化结果，并额外返回相对当前配方是否过期。

## 8. 获取材料目录

### `GET /api/v1/scent-blocks`

查询：

- `libraryVersion`；
- `family`；
- `mode=ordinary|advanced`。

普通模式响应材料字段：

```json
{
  "blockId": "SB-CA-01",
  "displayName": "佛手柑亮光",
  "family": "citrus_air",
  "description": "清亮、微苦、像晨光切开空气",
  "stageAffinity": ["top"],
  "strengthLabel": "medium_high",
  "realMaterialDirection": "佛手柑精油、柑橘型天然或合成香材及清亮柑橘香基",
  "materialDirectionDisclaimer": "代表性现实实现方向，不是完整生产配方"
}
```

禁止返回：

- 生产供应商；
- 批次；
- 精确生产比例；
- 熟化参数；
- 打样修正；
- 私有安全评估结果。

## 9. 版本接口

### 保存快照

`POST /api/v1/formulas/{formulaId}/versions`

请求必须指定：

- `expectedRevision`；
- `analysisId`；
- `label`可选。

分析 revision 与配方 revision 不一致时返回 `ANALYSIS_STALE`。

### 列表

`GET /api/v1/formulas/{formulaId}/versions`

### 分支

`POST /api/v1/formula-versions/{versionId}/branches`

创建新公式，继承不可变快照和分析，不继承原公式未来变化。

## 10. 封存

封存走 `formula.seal` 命令。

要求：

- 最新完整分析为fresh；
- 用户接受预测声明；
- 标题模式合法；
- 成功后当前版本不可直接修改。

## 11. Spec Manifest

### `GET /api/v1/spec-manifest`

响应：

```json
{
  "engine": {"version": "0.1.0", "sha256": "..."},
  "blocks": {"version": "0.1.0", "sha256": "..."},
  "relations": {"version": "0.1.0", "sha256": "..."},
  "protocol": {"version": "0.1.0"},
  "workbenchStateMachine": {"version": "0.1.0"}
}
```

用于客户端诊断和历史回放，不用于客户端自行实现另一套计算。

## 12. 错误契约

示例：

```json
{
  "type": "https://myscent/errors/revision-conflict",
  "title": "配方版本冲突",
  "status": 409,
  "detail": "当前配方已被其他命令更新。",
  "code": "REVISION_CONFLICT",
  "formulaId": "01J...",
  "expectedRevision": 6,
  "currentRevision": 7,
  "remoteDelta": {},
  "correlationId": "01J..."
}
```

HTTP映射建议：

| 错误码 | HTTP |
|---|---|
| INVALID_QUANTITY | 400 |
| BLOCK_NOT_FOUND | 400/404 |
| COMPONENT_NOT_FOUND | 404 |
| COMPONENT_ALREADY_EXISTS | 409 |
| REVISION_CONFLICT | 409 |
| DUPLICATE_COMMAND_MISMATCH | 409 |
| FORMULA_SEALED | 409 |
| ANALYSIS_STALE | 409 |
| UNDO_EMPTY | 409 |
| FORMULA_NOT_FOUND | 404 |
| ENGINE_INPUT_INVALID | 422 |
| INTERNAL_ERROR | 500 |

## 13. 幂等与重试

- 修改命令：使用 `idempotencyKey`；
- 分析请求：使用独立 `analysisIdempotencyKey`；
- 客户端超时后可以安全重试同一键；
- 同键不同请求体返回冲突；
- 成功响应可从幂等记录返回；
- 前端不得因重试重复播放配方完成或加料成功结算。

## 14. 访问控制

P0-A最低规则：

- 公式由owner或guest作用域访问；
- guest ID必须使用高熵随机值；
- 登录迁移属于P0-C，但数据模型保留owner/guest；
- 客户端只拿到当前体验层允许的材料字段；
- 生产层DTO和创作层DTO分离；
- 日志不记录完整私人配方响应。

## 15. 契约版本治理

- URL大版本：`/api/v1`；
- 计算、积木、关系和命令协议独立版本；
- 新增兼容字段不改变API大版本；
- 删除/改义字段需要API版本升级；
- 历史分析保存原规格版本；
- 客户端遇到不支持的协议版本应阻止修改，而不是猜测字段。

## 16. P0-A接口验收

1. 创建配方返回revision 0；
2. 每个成功修改命令revision恰好+1；
3. 幂等重试不增加revision；
4. revision冲突返回409且不修改配方；
5. Replace原子完成；
6. Undo恢复公式hash；
7. 分析绑定请求revision；
8. 迟到分析标记stale；
9. 场景字段不改变hash；
10. 普通目录接口不返回生产层字段；
11. Problem Details包含稳定code和correlationId；
12. 历史版本能够按原规格版本读取。