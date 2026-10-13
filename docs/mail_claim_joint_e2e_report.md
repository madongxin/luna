# 邮件领取回执联调

客户端只改了 `luna`。没有打开 webserver 仓库，没有 GameDB 流水，没有两个测试角色。`docs/mail_claim_e2e_runbook.md` 不在本仓库。

协议文本已经和 `webserver` `09dc21f` 的 `proto/game.proto` 对齐。这不等于 MAIL-E2E-01 的运行时往返已经执行，也不等于后面的实机项通过。

| 编号 | 结果 | 实际 |
| --- | --- | --- |
| MAIL-E2E-01 | BLOCKED | schema 哈希一致。C# 往返测试已写，Unity 工程被占用，本轮没跑 |
| MAIL-E2E-02 | BLOCKED | 没有角色 A 的领取和数据库流水 |
| MAIL-E2E-03 | BLOCKED | 详情解析已接上，没有实包 |
| MAIL-E2E-04 | BLOCKED | 批领会逐封读 receipt。没有部分成功的实库 |
| MAIL-E2E-05 | BLOCKED | 不确定结果仍留原键。没有同键实库 |
| MAIL-E2E-06 | BLOCKED | 后到的事务号不会覆盖第一份回执。没有实包 |
| MAIL-E2E-07 | BLOCKED | 无回执时版本前进不会显示到账。本轮测试未执行 |
| MAIL-E2E-08 | BLOCKED | 已保存的 `claim_tx_id` 和原始版本不被后一次奖励改写。未执行 |
| MAIL-E2E-09 | BLOCKED | 快照未追上时显示「邮件已领取，到账确认中」。未执行 |
| MAIL-E2E-10 | BLOCKED | 超时和 `STATE_SYNC_REQUIRED` 不换键，并再查详情。未执行 |
| MAIL-E2E-11 | BLOCKED | 非法附件和满包不记成功回执。没有回滚实库 |
| MAIL-E2E-12 | BLOCKED | 没有 GameLogic 内存不同步的环境 |
| MAIL-E2E-13 | BLOCKED | 没有角色 B |
| MAIL-E2E-14 | BLOCKED | 回执不带别人的 `player_id`，客户端只用会话角色。没有越权实包 |
| MAIL-E2E-15 | BLOCKED | `Clear` 会丢掉回执。没有切角色实机 |
| MAIL-E2E-16 | NOT_RUN | Redis / MySQL 故障未注入 |
| MAIL-E2E-17 | NOT_RUN | 没有第二个 GameLogic |
| MAIL-E2E-18 | NOT_RUN | outbox 重发未做 |
| MAIL-E2E-19 | NOT_RUN | 客户端不发送 `MailDeliver` |
| MAIL-E2E-20 | BLOCKED | 没有双账号和同一笔 `claim_tx_id` 的库证 |
| MAIL-E2E-21 | BLOCKED | `verifiable=false` 或 `UNKNOWN` 不记凭证。测试未执行 |
| MAIL-E2E-22 | BLOCKED | 上线结论：不可上线 |

没有 `mail_id`、`claim_tx_id`、入账版本和背包前后数量可以并排。下一步需要关掉本机正在占用工程的 Unity 后再跑 EditMode，并设置 `GAMEMESH_E2E_GATEWAY` 与两个隔离角色。
