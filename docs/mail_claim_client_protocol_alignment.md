# 邮件领取回执协议对齐

客户端 `main` 在改之前是 `5001fc1a5c66fe85b03fe33a9dd74255eb1d1bb6`。服务端 `https://github.com/madongxin/webserver` 的 `main` 是 `09dc21f78640b152291fb6adedffff1c0666b1af`。本仓库没有 `docs/mail_claim_receipt_contract.md`。对照的是该提交上的 `proto/game.proto`。

换行归一后，客户端 `Assets/GameMesh/Protocol/Schema/game.proto` 与这份服务端 proto 的 SHA-256 都是 `49479bbdb5f8f1c5e5a0b649f21bb5c265122b01f4656b18638c87a8f6102792`。C# 用 `Tools/GameMesh/generate_csharp_proto.ps1`（protoc 25.3）重新生成，没有手改 `Game.cs`。

## 回执字段

| 消息 | 字段 | 编号 | 类型 |
| --- | --- | --- | --- |
| `MailClaimGrant` | `asset_id` | 1 | `uint64` |
| `MailClaimGrant` | `count` | 2 | `uint32` |
| `MailClaimReceipt` | `claim_tx_id` | 1 | `string` |
| `MailClaimReceipt` | `asset_version_at_claim` | 2 | `uint64` |
| `MailClaimReceipt` | `grants` | 3 | `repeated MailClaimGrant` |
| `MailClaimReceipt` | `claim_state` | 4 | `string`：`UNCLAIMED` / `IN_PROGRESS` / `CLAIMED` / `UNKNOWN` |
| `MailClaimReceipt` | `verifiable` | 5 | `bool` |
| `MailDetail` | `receipt` | 5 | `MailClaimReceipt` |
| `MailClaimResult` | `receipt` | 6 | `MailClaimReceipt` |

回执里没有 `player_id`。客户端只用当前会话角色，加上 `MailClaimResult.mail_id` 或详情里的 `brief.mail_id`。

## 客户端怎么用

`ClaimAsync`、`BatchClaimAsync` 和 `GetAsync` 读到 `receipt` 后才调用 `AcceptReceipt`。`verifiable` 不为真、`claim_state` 不是 `CLAIMED`、事务号为空、版本为 0 或没有数量大于 0 的 grant，都不记成到账凭证。已有凭证时，后来的事务号和版本不会覆盖。

没有凭证时显示「邮件已领取，到账确认中」，并再拉一次 `MailGet`。明确不可验证时显示「领取状态已确认，到账凭证暂不可验证」。背包版本变大本身不会确认邮件。快照版本追上这封回执里的 `asset_version_at_claim` 之后，才显示「已到账」。

旧包没有 `receipt` 时，这些字段按 protobuf 缺省为空，不会被当成成功凭证。
