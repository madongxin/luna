# 邮件奖励协议对齐（客户端）

基线 `017771551b590ead1c0a8bd515795c5227128137`。本仓库没有服务端 `docs/mail_reward_integration_contract.md`，也没有 `docs/mail_protocol_contract.md`。协议只认 `Assets/GameMesh/Protocol/Schema/game.proto`。没有改 proto，没有手改 `Game.cs`。

## 已接入的现有消息

| 用途 | 消息 | 字段号 | 客户端用法 |
| --- | --- | --- | --- |
| 请求权威快照 | `GameRequest.world_snapshot` | 72 | `WorldSnapshotReq.player_id = 1`。领取后、打开邮箱、以及原有登录/重连路径都会走到它 |
| 权威快照回包 | `GameResponse.full_snapshot` | 54 | `FullStateSnapshotRsp` |
| 资产版本 | `asset_version` | 4 | 只有比本角色上一份快照更大时，才把待确认领取标成「已到账」 |
| 道具清单 | `item_ids` / `item_counts` | 5 / 6 | 整表替换。长度不一致则丢弃这份快照。重复 id 以后一条为准，不累加 |
| 角色 | `player_id` | 3 | 与当前会话角色不一致则整份丢弃 |
| 单领 | `MailClaimReq` | `mail_claim = 34` | `idempotency_key` 与 `trace_id` 都用同一把角色内稳定键 |
| 批领 | `MailBatchClaimReq` | `mail_batch_claim = 35` | 前缀按排序后的邮件 id 集合。`MailBatchClaimRsp.ok == false` 时仍然逐封读 `results` |

`FlushBagReq` 只表示断线刷队列，回包没有道具数量。`GrantItemReq` 会由客户端自己发奖，邮件领取不发送它，也不发送 `MailDeliver`。

## 版本策略

1. 第一次看到某角色的快照只建立基线，不显示「已到账」。
2. 之后 `asset_version` 变大：替换背包，并把本会话里已是 `CLAIMED` 且仍在「同步中 / 结果未确认」的邮件标成「已到账」。
3. 版本相同：再替换一次，数量不变，不重复加道具，也不当成新到账。
4. 版本变小或跳号：仍用这份完整清单替换，不把差额累加到旧数量上，也不当成这次领取已到账。
5. 登出、`Mail.Clear`、换角色后旧快照作废。

`mailbox_version` 仍只用于邮箱列表，不作为资产权威。

## 领取状态

| 状态 | 何时 | 幂等键 | 界面 |
| --- | --- | --- | --- |
| 领取中 | 请求尚未返回 | 保留 | 「领取中」 |
| 结果未确认 | 超时、`STATE_SYNC_REQUIRED`、`CLAIM_IN_PROGRESS`、批领没有 `results` | 保留 | 「领取结果未确认，正在同步背包」 |
| 邮件已领、背包未确认 | 邮件结果已是 `CLAIMED`，但资产版本没有前进 | 单领成功会释放邮件键；资产仍等下一份快照 | 「邮件已领取，背包同步中」 |
| 已到账 | 邮件已是 `CLAIMED`，且之后的快照版本变大 | 已释放 | 「已到账」 |
| 背包已满 | `INVENTORY_FULL` 或错误码里带 `BAG_FULL` | 单领释放；批领保留该组键以便原样重试 | 「背包已满」 |
| 可重试失败 | 其它明确业务失败 | 单领释放 | 「领取失败，可重试」 |
| 已领过 | `ALREADY_CLAIMED` | 释放，不本地加道具 | 版本没前进时仍是同步中 |

没有邮件领取流水或 `operation_id` 对应的资产回执。版本变大只说明权威背包更新了，不能单独证明这封邮件是唯一来源。

## 还缺的服务端字段

不新增消息号。需要服务端在现有合同里写明，或沿用现有快照：

- `WorldSnapshotReq` 的回包在领奖后是否带上当前 `asset_version`、`item_ids`、`item_counts`。
- 若领奖不提升 `asset_version`，需要一份已有的、能按角色 + `mail_id` + `idempotency_key` 查询的最终结果。客户端不会猜新的 RPC。
- `MailBatchClaimRsp.ok` 的定义：客户端已按「只要 `results` 非空就逐封处理」实现。
