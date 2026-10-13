# 邮件领取回执客户端验收

| 项 | 值 |
| --- | --- |
| 仓库 | `https://github.com/madongxin/luna`，分支 `main` |
| 改之前 HEAD | `5001fc1a5c66fe85b03fe33a9dd74255eb1d1bb6` |
| 远端 | `git fetch` 成功，改之前与 `origin/main` 一致 |
| Unity | `2022.3.62f3c1` |
| 服务端 proto | `madongxin/webserver` `09dc21f78640b152291fb6adedffff1c0666b1af` |
| 连接 | `GAMEMESH_E2E_GATEWAY` 未设置 |

## 上一轮缺口

| 问题 | 当前 |
| --- | --- |
| 客户端 proto 没有 `MailClaimReceipt` | 已按服务端 proto 补上，schema 哈希一致 |
| `ClaimAsync` 不读响应里的 receipt | 已从单领、批领和 `MailGet` 解析 |
| 背包版本前进就算到账 | 仍然只认这一封自己的回执 |
| 模拟 `AcceptReceipt` 不等于线上报文 | 已补二进制往返测试，本轮没有跑完 |

## 本轮测试

计划命令：

`Unity.exe -batchmode -nographics -projectPath C:\Users\dongx\FirstFPS -runTests -testPlatform EditMode -testResults Logs/editmode-mail9.xml -logFile Logs/editmode-mail9.log`

结果：**NOT_RUN**。Hub 里的 Unity 已经打开这个工程（进程从 `2026-10-13 15:30:18` 起）。第二次 batchmode 在 `HandleProjectAlreadyOpenInAnotherInstance` 崩溃。日志：`Logs/editmode-mail9.log`。没有 `editmode-mail9.xml`。不能把上一轮 `143/143` 算成本轮通过。

PlayMode：**NOT_RUN**。双账号：**BLOCKED**。

## 状态

功能已实现：单领、批领和详情会读取 `MailClaimResult.receipt` / `MailDetail.receipt`。自动测试本轮未执行。真实双端联调未通过。

到账仍要三件事同时成立：当前角色、这一封邮件的可验证 `CLAIMED` 回执、权威快照版本不早于 `asset_version_at_claim`。
