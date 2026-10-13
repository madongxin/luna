# 邮件奖励协议对齐（客户端）

基线 `eff72d02a678ec0e7970f72b78fe2f1ad42712ad`。本仓库没有服务端 `docs/mail_reward_integration_contract.md`。协议只认 `Assets/GameMesh/Protocol/Schema/game.proto`。这次没有改 proto，也没有手改 `Game.cs`，没有给领取回执分配字段号。

## 现有消息继续使用

| 用途 | 消息 | 字段号 |
| --- | --- | --- |
| 权威背包快照 | `GameRequest.world_snapshot` | 72，回包 `GameResponse.full_snapshot` = 54 |
| 资产版本和清单 | `asset_version` / `item_ids` / `item_counts` | 4 / 5 / 6 |
| 单领 | `mail_claim` | 34 |
| 批领 | `mail_batch_claim` | 35 |
| 附件数量 | `MailAttachmentDto.count` | 4，类型 `uint32` |

`asset_id` 是 `uint64`，`count` 是 `uint32`。预览按附件逐行用十进制原文显示，不把多行数量加进 `int`，也不合并成一次发奖。

世界快照只替换整包显示。版本变大、重复、回退都不会把某一封邮件标成「已到账」。

## 到账判据

一封邮件要同时满足下面三条才显示「已到账」：

1. 回执的 `player_id` 就是当前会话角色。
2. 回执的 `mail_id` 就是这一封，且带有非空 `claim_tx_id`、大于 0 的 `asset_version_at_claim`，以及至少一条 grant。
3. 当前角色已经看过一份权威快照，其 `asset_version` 不早于该回执里的版本。

同一封邮件后来再来的事务号或更新的版本不会覆盖第一次回执。另一封邮件的回执不能确认这一封。没有回执时，即使邮件已是 `CLAIMED`、背包版本也变大，文案仍是「领取状态已确认，到账凭证暂不可验证」。有回执但快照还没到那一版，文案是「已领取，到账确认中」。

回执不会往本地背包加数量。不确定结果仍保留原幂等键。`INVENTORY_FULL` 与 `INVALID_ATTACHMENT` 只显示服务端错误，不把邮件标成已领。

## 服务端还要冻结的回执

客户端入口是 `MailClient.AcceptReceipt`。它只接受已经对过账的语义，不是新的网络消息。服务端给出字段号之前，客户端不会发查询 RPC。

建议沿用下面的语义，由服务端选定未占用的字段号：

| 语义 | 要求 |
| --- | --- |
| `player_id` | 只承认已认证会话里的本人 |
| `mail_id` | 只确认这一封 |
| `claim_tx_id` | 首次成功提交的资产事务号，重试和 `ALREADY_CLAIMED` 都返回这一次 |
| `asset_version_at_claim` | 那一次入账时的版本，不用后来任务或商城把版本抬高后的当前值 |
| `grants` | 首次入账的物品 id 和 `uint32` 数量，同物品多附件不要在客户端合并 |
| `claim_state` | 只有明确的成功提交才能进入到账确认 |

`FlushBag` 和 `GrantItem` 仍不用于邮件领奖。
