# 邮件系统客户端验收

本地 `main` 与 `origin/main` 都是 `41bde448e9ec275af9c507c8aadc9f911ec93cb8`。`git fetch --prune origin` 成功，没有新提交。Unity `2022.3.62f3c1`。协议以本地 `Assets/GameMesh/Protocol/Schema/game.proto` 为准，导出摘要 `47e6fbf688af61b31ad4d5e2ecf88a17e9891521ad9950bb79076456a87a8d1a`。本工作区没有 webserver，也没有读到服务端 `docs/mail_protocol_contract.md`。

结论：**部分通过**。EditMode 131/131 通过。没有双账号实机，不能写成端到端已通过。

## 功能映射

入口是右上角 `MailScreen`，点「邮箱」展开，再点「收起」或改开好友时收起。没有邮件 Prefab，也没有新场景。没有另做一套 MailManager。

| 功能 | 位置 | 协议 | 这次 |
| --- | --- | --- | --- |
| 入口和红点 | `MailScreen` 右上角按钮。红点看 `HasBadge` | `MailboxSummary` | 未读只加 `unread_system + unread_activity`。另有未领取附件时按钮显示「附件」。社交、交易未读不进红点。`BuildOnce` 只建一次，反复开关不重复注册 |
| 列表 | `MailClient.RefreshAsync`，行上有发件人、标题、发送时间、未读、附件、收藏、到期 | `MailList` | 每页 20，最多 20 页。空页带游标、游标重复、筛选已变，都不替换当前列表。`mailbox_version` 变小仍用这次完整快照对账，因为服务端版本不一定跨进程递增。相同 `mail_id` 只留一条。加载中显示「加载中」 |
| 筛选 | 全部 / 系统 / 活动，以及未读、未领取、即将过期、收藏 | `category`、`unread`、`unclaimed`、`expiring_soon`、`favorite` | 不显示 SOCIAL / TRADE 页。分类字符串用策划案里的 `SYSTEM`、`ACTIVITY` |
| 详情 | 点列表走 `OpenAsync` | `MailGet`，需要时再 `MailRead` | 正文去掉尖括号后再画。附件没有本地图鉴时显示「未知物品」或「物品 #id」 |
| 已读 / 收藏 / 删除 | `ReadAsync`、`FavoriteAsync`、`BatchReadAsync`、`BatchDeleteAsync` | 对应 Req，带幂等键 | 按钮只认 `allowed_actions` 里大小写不敏感的 `read`、`claim`、`favorite`、`delete`。协议没有列出合法取值，别的词不显示按钮 |
| 领取 | `ClaimAsync`、`BatchClaimAsync` | `MailClaim`、`MailBatchClaim` | 键带角色 id、操作和邮件 id。超时不换键。成功后再拉摘要和列表。`STATE_SYNC` 只提示同步，不发 `GrantItem`。超过 20 封按 20 一组分批，每组自己的前缀。失败项留在 `ClaimFailures` |
| 登录 / 重连 | `GameMeshClient` 登录成功、已登录跳过、重连成功 | 摘要加列表 | 和好友刷新并列。世界快照失败时仍然拉邮箱 |
| 换角 / 登出 | `Mail.Clear`，联调面板改玩家 ID 时同时清好友和邮箱 | 代次加一，刷新再对一下当时的角色 id | 旧角色回包不写入新角色。进行中的刷新只再补一轮，不并行开多轮 |
| 推送 | `NotifyMailboxChanged` | `MailboxChanged` | 只触发一次防抖刷新，不在本地把红点加一 |
| 写信 / 内网投递 | 界面上没有写信框 | `PlayerMailSend` 仍留在 `SendAsync`；`MailDeliver` 客户端不发 | V1 界面不调用写信。`SendAsync` 留给原有自动场景和 `MailSend_KeepsOperationIdUntilTerminal` |

## 调用和错误

所有请求仍走 `GameMeshClient.RequestAsync`。成功要看响应的 `ok`，领取还要看 `MailClaimResult`。请求发出去不等于领取成功，也不等于道具已进背包。

| 调用 | 失败时 |
| --- | --- |
| 摘要 | 保留原列表，只写错误 |
| 列表 | 中途失败、空页带游标、游标重复、筛选已变：不替换。版本倒退的完整快照会替换 |
| 详情 | 不改未读数 |
| 已读 | 服务器失败不减未读。批量已读只有全部成功才在本地标已读 |
| 单领 | 无 `claim` 或状态已是 `CLAIMING` 时不发新请求。超时保留原键并刷新。错误码包含 `STATE_SYNC` 时提示「奖励已处理，正在同步背包」 |
| 一键领取 | 每批最多 20 封，多出来的下一批再发。状态为未填、`UNCLAIMED`、`PARTIAL`。跳过 `CLAIMING`。失败项留在 `ClaimFailures` |
| 收藏 | 以回包 `is_favorite` 为准 |
| 删除 | 未领取附件且详情不允许 `delete` 时不发请求，提示「未领取附件不能删除」。`failed_mail_ids` 留在列表里 |

倒计时用最近一次 `server_time_utc` 加上 `Time.unscaledTime` 的增量。72 小时内显示「即将过期」。不读设备时钟。

## 登录、重连、换角

1. 登录成功，或连接已经处于已登录状态而跳过登录时，调用 `Mail.RefreshAsync`。
2. 重连成功后同样刷新。`need_full_snapshot` 仍只表示世界快照；世界快照失败时也刷新邮箱。
3. 登出和会话被替换时原有 `Mail.Clear` 清列表、红点和幂等键，并增加代次。
4. `MailboxChanged` 不当作全量邮件，只安排下一次摘要和列表刷新。

## 测试

命令：

`Unity.exe -batchmode -nographics -projectPath C:\Users\dongx\FirstFPS -runTests -testPlatform EditMode -testResults Logs/editmode-mail4.xml -logFile Logs/editmode-mail4.log`

结果：`total=131`，`passed=131`，`failed=0`。时间 `2026-10-12 05:44:37Z`。

| 编号 | 结果 | 证据 |
| --- | --- | --- |
| MAIL-01 | 未实机 | `Refresh_ZeroOneAndManyPages_ReplacesOnlyAfterTheLastPage` 覆盖摘要、多页和去重。在线收信没有实机 |
| MAIL-02 | 未实机 | 登录会拉摘要和列表，不依赖历史推送。离线重登没有实机 |
| MAIL-03 | 通过 | 同一页相同 `mail_id` 只保留后一条 |
| MAIL-04 | 通过 | `Claim_DoubleClickAndTimeout_ReuseOneKey_SyncDoesNotInventItems`。没有真实背包入账 |
| MAIL-05 | 未实机 | 超时会刷新并保留原键。断线后的背包没有实机 |
| MAIL-06 | 未实机 | 未知错误走 `GameErrorCatalog`。没有故障注入 |
| MAIL-07 | 通过 | 未知物品显示「未知物品」。不本地删附件。背包满只展示服务端错误文案 |
| MAIL-08 | 通过 | `BatchClaim_KeepsFailuresSeparate`、`BatchClaim_SplitsAfterTwenty` |
| MAIL-09 | 通过 | `DeleteWithoutDeleteAction_DoesNotRemoveOrSend` |
| MAIL-10 | 部分 | 界面显示摘要里的 `current_count/max_capacity`。邮箱满的实机未跑 |
| MAIL-11 | 通过 | `ExpiryAndUnknownItem_UseServerTimeAndDoNotInventNames`。过期扫描后的实刷新未跑 |
| MAIL-12 | 通过 | `Clear_DropsALateList`、`PlayerChangedDuringRefresh_KeepsPreviousList`、`VersionReset_ReplacesInbox_AndClaimKeyFollowsPlayer` |
| MAIL-13 | 通过 | 空页、重复游标、筛选变化、版本倒退、`OverlappingRefresh_RunsOneExtraPass`、`LargeMailId_RoundTripsAsUlong`。推送只防抖，不把红点加一 |
| MAIL-14 | 部分 | 会话失效仍走原有 `RequestAsync` / `HandleSessionReplaced`，并 `Mail.Clear`。没有单独的未登录点击用例 |
| MAIL-15 | 通过 | `BatchReadAndFavorite_FollowTheServerResult`、`Delete_FailedIdStays_ReadFailureDoesNotDropUnread` |
| MAIL-16 | 通过 | 收件操作的测试请求里没有 `MailDeliver`，也没有 `GrantItem`。运行时代码不构造这两类请求 |
| MAIL-17 | 通过 | 幂等键包含角色 id。换角色后再领同一封邮件会用新键。服务端拒绝冲突键没有实机 |
| MAIL-18 | 部分 | `VersionReset_ReplacesInbox_AndClaimKeyFollowsPlayer`：版本从 50 回到 2 仍换成新列表。多实例重启没有实机 |

PlayMode **NOT RUN**。双账号 **BLOCKED**：没有邮件实机脚本，`GAMEMESH_E2E_GATEWAY` 未设置。

`git diff --check` 无输出。

## 本次修改

- `MailClient.cs`：刷新进行中再开一轮只补一次。回包如果角色已经换了，不写入。版本倒退仍接受完整快照。领取成功后再刷新。一键领取按 20 封分批。幂等键带上角色 id。
- `MailScreen.cs`：分页中、领取中、已领取、同步中、断线时标明待同步。
- `GameMeshRuntimeUi.cs`：手改玩家 ID 时清掉邮箱和好友。
- `MailClientTests.cs`：补角色切换、大整数邮件 id、重叠刷新。
- 没有改 `game.proto`、`Game.cs`、场景或 Prefab。

## 仍缺

- 服务端协议合同不在本仓库，`allowed_actions` 的正式词表未在 proto 里列出。客户端只匹配 `read` / `claim` / `favorite` / `delete`。
- 没有物品图鉴。附件只有编号和数量。
- 客户端运行时没有背包快照接口。领取成功只再拉邮箱，不本地加道具。背包是否入账要等服务端现有背包同步，不能在这里伪造。
- `SendAsync` 仍能发 `PlayerMailSend`，只是 V1 界面不再调用。这是 V2 写信，不是 `MailDeliver`。
- 没有双账号日志，上线前需要真实 Gateway 走一遍：系统投递、登录、已读、单领、一键领取、收藏、删除、离线重登、换角。
