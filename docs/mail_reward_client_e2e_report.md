# 邮件奖励一致性客户端报告

结论：**奖励一致性和双端联调未完成**。EditMode 里的模拟快照不能代替 GameDB 入账。

| 项 | 值 |
| --- | --- |
| 仓库 | `https://github.com/madongxin/luna` |
| 改之前 HEAD | `017771551b590ead1c0a8bd515795c5227128137` |
| Unity | `2022.3.62f3c1` |
| 协议 | 本地 `Assets/GameMesh/Protocol/Schema/game.proto`。导出摘要 `47e6fbf688af61b31ad4d5e2ecf88a17e9891521ad9950bb79076456a87a8d1a` |
| 连接环境 | `GAMEMESH_E2E_GATEWAY` 未设置。没有测试角色，没有 GameDB |
| 服务端合同 | 工作区里没有 `docs/mail_reward_integration_contract.md` |

## 这次实际跑过的测试

EditMode：

`Unity.exe -batchmode -nographics -projectPath C:\Users\dongx\FirstFPS -runTests -testPlatform EditMode -testResults Logs/editmode-mail7.xml -logFile Logs/editmode-mail7.log`

结果：`total=139`，`passed=139`，`failed=0`。时间 `2026-10-13 03:57:12Z`。日志在 `Logs/editmode-mail7.xml`。

PlayMode：**NOT_RUN**。没有真实 Gateway 的 PlayMode 用例。

真实领取到账案例：**0**。没有前后权威背包、邮件附件和资产流水可以对照。

## REWARD 编号

执行环境都是这台机器。源码 SHA 是上表的改之前 HEAD；提交后的 SHA 以 `git rev-parse HEAD` 为准。预期都是真实双角色加 GameDB 对账。实际没有这条链路，所以没有一项可以写成 PASS。

| ID | 结果 | 实际 | 证据 |
| --- | --- | --- | --- |
| REWARD-01 | BLOCKED | 没有在线投递 | 缺 Gateway 与角色 A |
| REWARD-02 | BLOCKED | 没有离线重登 | 同上，角色 B |
| REWARD-03 | BLOCKED | 单领只在 EditMode 里看模拟快照 | `Claim_ShowsArrivedOnlyAfterAssetVersionAdvances`。无数据库 |
| REWARD-04 | BLOCKED | 同 key 重试有 EditMode，没有资产只入账一次的库证 | `ClaimSync_KeepsTheSameKey` |
| REWARD-05 | NOT_RUN | 不同 operation_id 并发要服务端拒绝第二次入账 | 客户端不生成第二把键去抢同一封 |
| REWARD-06 | BLOCKED | 双击只发一把键。没有服务端资产计数 | `Claim_DoubleClickAndTimeout_ReuseOneKey_SyncDoesNotInventItems` |
| REWARD-07 | BLOCKED | 超时保留原键。没有丢响应后的库证 | 同上 |
| REWARD-08 | BLOCKED | `STATE_SYNC_REQUIRED` 保留原键并再拉快照。没有 GameDB Commit | `ClaimSync_KeepsTheSameKey` |
| REWARD-09 | NOT_RUN | 事务回滚需要服务端故障注入 | 客户端不把失败标成 `CLAIMED` |
| REWARD-10 | BLOCKED | 结果未知会再拉 `full_snapshot`。没有操作查询接口的实包 | `FinishClaimRecoveryAsync` |
| REWARD-11 | BLOCKED | `INVENTORY_FULL` 显示「背包已满」且不本地加数量。没有满包实机 | `BatchClaim_AllFailedRowsStayVisible_AndRetryReusesTheKey` |
| REWARD-12 | NOT_RUN | 一键全成功的逐道具资产变化没有实库 | 版本没前进时文案停在同步中 |
| REWARD-13 | BLOCKED | 全部失败仍逐封展示，不写「全部成功」。没有实库 | 同上批领测试 |
| REWARD-14 | NOT_RUN | Redis 故障未注入 | 会话替换仍走原有 `Mail.Clear` |
| REWARD-15 | NOT_RUN | MySQL 暂停与重启未做 | — |
| REWARD-16 | NOT_RUN | 过期并发未做 | — |
| REWARD-17 | BLOCKED | 重连仍拉邮箱；世界快照会喂给背包视图。没有断网实机 | `GameMeshClient` 登录/重连刷新，`ObserveBag` |
| REWARD-18 | BLOCKED | 换角色会清背包视图。没有双账号 | `Clear_DropsTheBagForTheNextCharacter` |
| REWARD-19 | BLOCKED | 重复版本不累加，回退版本只替换不确认到账。没有实推送 | `BagSnapshot_ReplacesCounts_AndIgnoresTheWrongPlayer` |
| REWARD-20 | NOT_RUN | 没有第二个 GameLogic | 客户端不用 `mailbox_version` 当资产权威 |
| REWARD-21 | NOT_RUN | 伪造收件人要服务端鉴权 | 客户端不发 `MailDeliver` / `GrantItem` |
| REWARD-22 | NOT_RUN | outbox 补发未做 | 重复快照不累加 |
| REWARD-23 | BLOCKED | proto 字段已按本地 schema 使用。Unity 双角色未跑 | `docs/mail_reward_client_protocol_alignment.md` |
| REWARD-24 | BLOCKED | 上线结论：不可上线 | 本文件 |

BLOCKED 18，NOT_RUN 6，PASS 0，FAIL 0。阻断项 REWARD-03 至 REWARD-11、REWARD-13、REWARD-17、REWARD-18、REWARD-21、REWARD-23 都没有真实通过。

## 已改文件

- `Assets/GameMesh/Runtime/Mail/AuthoritativeBag.cs`
- `Assets/GameMesh/Runtime/Mail/MailClient.cs`
- `Assets/GameMesh/Runtime/UI/MailScreen.cs`
- `Assets/GameMesh/Runtime/Bootstrap/GameMeshClient.cs`
- `Assets/GameMesh/Tests/EditMode/MailClientTests.cs`

## 下一步

1. 服务端交出 `docs/mail_reward_integration_contract.md`，确认领奖后的 `full_snapshot` 带 `asset_version` 和道具清单。
2. 准备两个隔离测试角色，设置 `GAMEMESH_E2E_GATEWAY`，再跑领取、重试、批领全失败和断线重连。
3. 用同一 `trace_id` / `mail_id` / `idempotency_key` / `asset_version` 对账。对不上就继续标待确认。
