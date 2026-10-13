# 邮件奖励一致性客户端报告

结论：**代码已收紧到按邮件回执确认，真实双端联调仍未完成。** 不能写成奖励一致性上线通过。

| 项 | 值 |
| --- | --- |
| 仓库 | `https://github.com/madongxin/luna` |
| 改之前 HEAD | `eff72d02a678ec0e7970f72b78fe2f1ad42712ad` |
| Unity | `2022.3.62f3c1` |
| 协议 | 本地 `game.proto`，导出摘要 `47e6fbf688af61b31ad4d5e2ecf88a17e9891521ad9950bb79076456a87a8d1a` |
| 连接 | `GAMEMESH_E2E_GATEWAY` 未设置。没有测试角色，没有 GameDB |
| 服务端合同 | 工作区里没有 `docs/mail_reward_integration_contract.md`，也没有 `docs/mail_e2e_runbook.md` |

## 这次实际跑过的测试

`Unity.exe -batchmode -nographics -projectPath C:\Users\dongx\FirstFPS -runTests -testPlatform EditMode -testResults Logs/editmode-mail8.xml -logFile Logs/editmode-mail8.log`

结果：`total=143`，`passed=143`，`failed=0`。时间 `2026-10-13 07:12:01Z`。

PlayMode：**NOT_RUN**。真实领取到账案例：**0**。

## 归因反例

这些只说明客户端状态机。回执是测试直接交给 `AcceptReceipt` 的，不是 Gateway 回包，不能写成 REWARD PASS。

| 编号 | 结果 | 实际 |
| --- | --- | --- |
| EDGE-01 | BLOCKED | 预览把 `uint32` 最大值和另一条同物品分行显示，没有相加。服务端整单回滚没有实库 |
| EDGE-02 | BLOCKED | 客户端不把数量收成 `int`。恰达边界能否入账要看服务端背包上限 |
| EDGE-03 | BLOCKED | 零数量、未知物品、不同物品各占一行。没有实库 |
| EDGE-04 | BLOCKED | `Receipt_ConfirmsOnlyThatMail_AndIgnoresLaterVersionBumps`：后一次版本 12 不改邮件 7 的原始版本 6。无数据库 |
| EDGE-05 | BLOCKED | 同一测试里，未领取邮件在版本前进后不是「已到账」 |
| EDGE-06 | BLOCKED | 邮件 7 和邮件 8 各用 `tx-m`、`tx-m2`。无商城实单 |
| EDGE-07 | BLOCKED | 第二次 `tx-later` 被拒绝，原事务号保留。无服务端 `ALREADY_CLAIMED` 实包 |
| EDGE-08 | BLOCKED | 错误角色的回执被拒绝，`Clear` 丢掉旧回执。无断线实机 |

## REWARD

执行环境是这台机器。没有一项真实双角色对账，所以没有 PASS。

| ID | 结果 | 说明 |
| --- | --- | --- |
| REWARD-01 | BLOCKED | 没有在线投递 |
| REWARD-02 | BLOCKED | 没有离线重登 |
| REWARD-03 | BLOCKED | 单领后若没有回执，只显示凭证不可验证 |
| REWARD-04 | BLOCKED | 不确定结果仍保留原键。没有同一回执的库证 |
| REWARD-05 | NOT_RUN | 不同键并发要服务端保证只入账一次 |
| REWARD-06 | BLOCKED | 双击只发一把键。没有资产计数 |
| REWARD-07 | BLOCKED | 超时保留原键。没有丢响应后的库证 |
| REWARD-08 | BLOCKED | `STATE_SYNC_REQUIRED` 不再因为背包版本变大就标到账 |
| REWARD-09 | NOT_RUN | 回滚需要服务端故障注入 |
| REWARD-10 | BLOCKED | 没有按邮件查询回执的协议，结果未知不能消歧 |
| REWARD-11 | BLOCKED | `INVALID_ATTACHMENT` / 满包只显示错误，不本地加数量。没有实库 |
| REWARD-12 | NOT_RUN | 批领全成功的逐封回执没有实库 |
| REWARD-13 | BLOCKED | 全部失败仍逐封展示，不写全部成功 |
| REWARD-14 | NOT_RUN | Redis 故障未注入 |
| REWARD-15 | NOT_RUN | MySQL 暂停与重启未做 |
| REWARD-16 | NOT_RUN | 过期并发未做 |
| REWARD-17 | BLOCKED | 重连会拉邮箱和世界快照。没有断网实机 |
| REWARD-18 | BLOCKED | 换角色会清回执和背包视图。没有双账号 |
| REWARD-19 | BLOCKED | 重复和回退快照不确认没有回执的邮件 |
| REWARD-20 | NOT_RUN | 没有第二个 GameLogic |
| REWARD-21 | NOT_RUN | 伪造收件人要服务端鉴权。客户端不发 `MailDeliver` / `GrantItem` |
| REWARD-22 | NOT_RUN | outbox 重发未做 |
| REWARD-23 | BLOCKED | Unity 双角色全链路未跑 |
| REWARD-24 | BLOCKED | 上线结论：不可上线 |

REWARD：BLOCKED 15，NOT_RUN 9，PASS 0，FAIL 0。EDGE：BLOCKED 8。阻断项没有真实通过。

## 已改文件

- `Assets/GameMesh/Runtime/Mail/MailClient.cs`
- `Assets/GameMesh/Runtime/UI/MailScreen.cs`
- `Assets/GameMesh/Tests/EditMode/MailClientTests.cs`
- `docs/mail_client_acceptance.md`
- `docs/mail_reward_client_protocol_alignment.md`
- `docs/mail_reward_client_e2e_report.md`
- `docs/mail_reward_joint_reconciliation.md`

## 下一步

服务端先冻结领取回执的字段号，并保证 `ALREADY_CLAIMED`、换键重试都返回首次 `claim_tx_id` 和 `asset_version_at_claim`。然后设置 `GAMEMESH_E2E_GATEWAY` 和两个隔离角色，再跑 REWARD-01 到 REWARD-24。
