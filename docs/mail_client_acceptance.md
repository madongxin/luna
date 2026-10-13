# 邮件系统客户端验收

改之前本地 `main` 与 `origin/main` 都是 `ca04d0ba1b2e3e4e19b4ec1d83cf6e10c92557e1`。`git fetch --prune origin` 成功，没有新提交。Unity `2022.3.62f3c1`。协议以本地 `Assets/GameMesh/Protocol/Schema/game.proto` 为准，导出摘要 `47e6fbf688af61b31ad4d5e2ecf88a17e9891521ad9950bb79076456a87a8d1a`。本工作区没有 webserver，也没有读到服务端 `docs/mail_protocol_contract.md`。

结论：**可联调，正式验收不通过**。客户端收藏权限和不确定领取的幂等键已按服务端词表对齐。没有背包权威接口，不能把「邮件已领取」写成道具已到账。没有双账号实机。

## 功能映射

入口是右上角 `MailScreen`，点「邮箱」展开，再点「收起」或改开好友时收起。没有邮件 Prefab，也没有新场景。没有另做一套 MailManager。

| 功能 | 位置 | 协议 | 这次 |
| --- | --- | --- | --- |
| 入口和红点 | `MailScreen` 右上角按钮。红点看 `HasBadge` | `MailboxSummary` | 未读只加 `unread_system + unread_activity`。另有未领取附件时按钮显示「附件」。社交、交易未读不进红点。`BuildOnce` 只建一次，反复开关不重复注册 |
| 列表 | `MailClient.RefreshAsync`，行上有发件人、标题、发送时间、未读、附件、收藏、到期 | `MailList` | 每页 20，最多 20 页。空页带游标、游标重复、筛选已变，都不替换当前列表。`mailbox_version` 变小仍用这次完整快照对账，因为服务端版本不一定跨进程递增。相同 `mail_id` 只留一条。加载中显示「加载中」 |
| 筛选 | 全部 / 系统 / 活动，以及未读、未领取、即将过期、收藏 | `category`、`unread`、`unclaimed`、`expiring_soon`、`favorite` | 不显示 SOCIAL / TRADE 页。分类字符串用策划案里的 `SYSTEM`、`ACTIVITY` |
| 详情 | 点列表走 `OpenAsync` | `MailGet`，需要时再 `MailRead` | 正文去掉尖括号后再画。附件没有本地图鉴时显示「未知物品」或「物品 #id」 |
| 已读 / 收藏 / 删除 | `ReadAsync`、`FavoriteAsync`、`BatchReadAsync`、`BatchDeleteAsync` | 对应 Req，带幂等键 | 未收藏且有 `FAVORITE` 才显示「收藏」。已收藏且有 `UNFAVORITE` 才显示「取消收藏」。批量已读、删除的键按排序后的邮件 id 集合区分。超时不换键 |
| 领取 | `ClaimAsync`、`BatchClaimAsync` | `MailClaim`、`MailBatchClaim` | 键带角色 id、操作和邮件 id。超时和 `STATE_SYNC_REQUIRED` 都不丢键。成功文案是「邮件已领取，背包尚未确认到账」。超过 20 封按 20 一组分批，每组键按这组 id 区分。失败项留在 `ClaimFailures` |
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
| 单领 | 无 `claim` 或状态已是 `CLAIMING` 时不发新请求。超时和 `STATE_SYNC_REQUIRED` 都保留原键并刷新。成功不写成道具已到账 |
| 一键领取 | 每批最多 20 封，多出来的下一批再发。同步未确认的那一组不丢键。有成功时文案带「背包尚未确认到账」 |
| 收藏 | 先看 `FAVORITE` / `UNFAVORITE` 是否允许这次方向，再以回包 `is_favorite` 为准。超时保留原键 |
| 删除 | 未领取附件且详情不允许 `delete` 时不发请求，提示「未领取附件不能删除」。`failed_mail_ids` 留在列表里 |

倒计时用最近一次 `server_time_utc` 加上 `Time.unscaledTime` 的增量。72 小时内显示「即将过期」。不读设备时钟。

## 登录、重连、换角

1. 登录成功，或连接已经处于已登录状态而跳过登录时，调用 `Mail.RefreshAsync`。
2. 重连成功后同样刷新。`need_full_snapshot` 仍只表示世界快照；世界快照失败时也刷新邮箱。
3. 登出和会话被替换时原有 `Mail.Clear` 清列表、红点和幂等键，并增加代次。
4. `MailboxChanged` 不当作全量邮件，只安排下一次摘要和列表刷新。

## 测试

命令：

`Unity.exe -batchmode -nographics -projectPath C:\Users\dongx\FirstFPS -runTests -testPlatform EditMode -testResults Logs/editmode-mail6.xml -logFile Logs/editmode-mail6.log`

结果：`total=134`，`passed=134`，`failed=0`。时间 `2026-10-13 02:36:04Z`。

编号按 2026-10-13 V7。旧编号不再沿用。

| 编号 | 结果 | 证据 |
| --- | --- | --- |
| MAIL-01 | 未执行 | 登录会拉摘要和列表。在线/离线投递没有实机 |
| MAIL-02 | 未执行 | 重复投递冲突是服务端行为。客户端只按 `mail_id` 去重 |
| MAIL-03 | 通过 | 空页、重复游标、筛选变化、`OverlappingRefresh_RunsOneExtraPass`、`LargeMailId_RoundTripsAsUlong` |
| MAIL-04 | 通过 | 详情失败写 `LastError`，不把失败当成无附件成功 |
| MAIL-05 | 通过 | 已读失败不减未读。`Delete_FailedIdStays_ReadFailureDoesNotDropUnread` |
| MAIL-06 | 通过 | `Favorite_UsesFavoriteOrUnfavorite_AndKeepsKeyOnTimeout`。`FAVORITE` / `UNFAVORITE` 分开判断 |
| MAIL-07 | 通过 | `Delete_FailedIdStays_ReadFailureDoesNotDropUnread`、`BatchDelete_KeyFollowsTheIdSet` |
| MAIL-08 | 通过 | `DeleteWithoutDeleteAction_DoesNotRemoveOrSend`。领取中的邮件不会在本地删掉 |
| MAIL-09 | 环境阻塞 | 领取后只刷新邮箱，文案是「邮件已领取，背包尚未确认到账」。运行时没有背包快照接口 |
| MAIL-10 | 部分 | `Claim_DoubleClickAndTimeout_ReuseOneKey_SyncDoesNotInventItems`、`ClaimSync_KeepsTheSameKey` 覆盖重试和同步保留原键。真实入账未跑 |
| MAIL-11 | 通过 | `BatchClaim_KeepsFailuresSeparate`、`BatchClaim_SplitsAfterTwenty`。背包变化未实机 |
| MAIL-12 | 部分 | 背包满只展示服务端错误，不本地加道具。真实背包未跑 |
| MAIL-13 | 未执行 | 没有 MySQL 故障注入 |
| MAIL-14 | 环境阻塞 | `STATE_SYNC_REQUIRED` 保留原键并刷新邮箱。没有 GameDB 超时环境 |
| MAIL-15 | 未执行 | 过期文案有 `ExpireLabel`。与领取并发没有实机 |
| MAIL-16 | 未执行 | 会话失效走原有 `HandleSessionReplaced`。Redis 故障未注入 |
| MAIL-17 | 部分 | 客户端不构造 `MailDeliver` 或 `GrantItem`。双账号未跑 |
| MAIL-18 | 部分 | `VersionReset_ReplacesInbox_AndClaimKeyFollowsPlayer`。多实例重启未跑 |
| MAIL-19 | 部分 | 登录、跳过登录、重连都会 `Mail.RefreshAsync`。漏推送实机未跑 |
| MAIL-20 | 部分 | `PlayerChangedDuringRefresh_KeepsPreviousList`、`Clear_DropsALateList`。双账号实机未跑 |
| MAIL-21 | 通过 | `OverlappingRefresh_RunsOneExtraPass`。推送只防抖，不把红点加一 |
| MAIL-22 | 环境阻塞 | 本仓库没有 `docs/mail_protocol_contract.md`。`allowed_actions` 按 `READ` / `CLAIM` / `FAVORITE` / `UNFAVORITE` / `DELETE` 做大小写不敏感精确匹配。双端联调未跑 |

PlayMode **NOT RUN**。双账号 **BLOCKED**：没有邮件实机脚本，`GAMEMESH_E2E_GATEWAY` 未设置。

`git diff --check` 无输出。

## 本次修改

- `MailClient.cs`：收藏按 `FAVORITE` / `UNFAVORITE`。`STATE_SYNC_REQUIRED` 和超时不丢领取键。批量已读、删除、领取的键按邮件 id 集合区分。领取成功不再写成道具已到账。
- `MailScreen.cs`：已收藏时显示「取消收藏」，未收藏时显示「收藏」。
- `MailClientTests.cs`：补收藏权限、同步保留键、删除键随 id 集合变化。
- 没有改 `game.proto`、`Game.cs`、场景或 Prefab。

## 仍缺

- 服务端协议合同不在本仓库。客户端按 `READ` / `CLAIM` / `FAVORITE` / `UNFAVORITE` / `DELETE` 精确匹配，未知词不开放按钮。
- 没有物品图鉴。附件只有编号和数量。
- 权威背包只认已有的 `WorldSnapshotReq` / `FullStateSnapshotRsp.asset_version`。没有版本前进时不显示「已到账」。真实双端对账见 `docs/mail_reward_client_e2e_report.md`。
- `SendAsync` 仍能发 `PlayerMailSend`，只是 V1 界面不再调用。这是 V2 写信，不是 `MailDeliver`。
- 没有双账号日志，上线前需要真实 Gateway 走一遍：系统投递、登录、已读、单领、一键领取、收藏、删除、离线重登、换角。
