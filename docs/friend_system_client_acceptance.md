# 好友系统客户端验收

本地与远端 `origin/main`：`c7abbc1`。`git fetch --prune origin` 成功，没有新提交。Unity `2022.3.62f3c1`。协议用本地 `Assets/GameMesh/Protocol/Schema/game.proto`，导出摘要 `47e6fbf6…`。webserver 仓库不在本工作区。

结论：**部分通过**。EditMode 110/110 通过。双账号实机没有跑，不能写成已完成 E2E。

## 功能映射

| 功能 | 已有位置 | 协议 | 这次 |
| --- | --- | --- | --- |
| 入口和面板 | `FriendScreen.Ensure`，运行时创建，右上角「好友」 | 不直接发协议 | 断线或重连保持期间，状态行显示「在线状态可能不是最新」 |
| 好友 / 申请 / 黑名单 | `FriendClient.ReadFriendsAsync` 等 | `FriendList`、`FriendRequestList`、`FriendBlockList` | 空页却带 `next_cursor`，或游标重复，都不替换已有列表 |
| 搜索和申请 | `SearchAsync`、`ApplyAsync` | `FriendSearch`、`FriendApply` | 一次点击结束后释放 `operation_id`；超时重试仍复用；下一次点击用新 id |
| 同意 / 拒绝 / 删除 / 拉黑 | 对应 `*Async` | 对应 Req，带 `operation_id` | 业务结果返回后同样释放 id |
| 推送 | `ApplyPush` | 四种好友 push | 删除之后迟到的在线推送不会把好友加回来 |
| 登录和重连 | `GameMeshClient.ReconnectAsync` 调 `RefreshAfterLoginAsync` | `need_full_snapshot` 只拉世界快照，好友另拉三份列表 | 世界快照失败时仍拉好友。关系推送作废进行中的列表后，最多再补拉两次 |
| 切角色 | `Friends.Clear` 增加会话代次 | 迟到回包按代次丢弃 | 补了 A 的回包不能写成 B 的列表 |
| 推送确认 | `GameMeshClient` 的 `PushAckReq` | `push_ack` | 未改 |
| 私聊 / 备注 / 资料页 | 无私聊界面，无备注写接口，无他人资料页 | 无 | 未编造 |

面板不是 Prefab。`BuildOnce` 只绑一次按钮。没有新增场景或 `.meta`。

## 同步顺序

1. 登录或重连成功后 `RefreshAfterLoginAsync`：好友、申请、黑名单都从空游标拉到尾，再整表替换。
2. 某一页失败：不替换已有快照。
3. 同步过程中又来一次全量刷新：记下再跑一轮，不并行开多轮。
4. 同步过程中的关系推送会推进列表代次，旧快照作废，并再补拉一轮，最多三次。拉列表期间更新的在线推送，在快照落地后按 `last_online_time` 合并，不回退。
5. 断线：`DataStale`。界面不把这份在线状态说成实时。
6. 登出或被挤下线：`Clear`，代次加一，列表和角标清空。

## 测试

命令：

`Unity.exe -batchmode -nographics -projectPath C:\Users\dongx\FirstFPS -runTests -testPlatform EditMode -testResults Logs/editmode-friends11.xml`

结果：`total=110`，`passed=110`，`failed=0`。时间 `2026-10-12 03:28:36Z`。

| ID | 结果 | 证据 |
| --- | --- | --- |
| C01 | 通过 | `EmptyPageWithCursor_KeepsPreviousFriends`、`EmptyRequestAndBlockPageWithCursor_KeepsPreviousLists`、`RepeatedFriendCursor_KeepsPreviousList` |
| C02 | 通过 | `Search_DebouncesAndAcceptsOddCharacters` |
| C03 | 通过 | `Apply_CompletedClickUsesNewOperationId`、`TransportTimeout_RetriesOnceWithSameOperationId` |
| C04 | 通过 | `Accept_InsertsPeerAndClearsRequest`、`ExpiredRequest_DoesNotSendAccept` |
| C05 | 通过 | `Block_RemovesFriendAndRequests_UnblockReversesSearchRelation` |
| C06 | 通过 | `Pushes_RemoveAddAndDedupeRequests_PresenceDoesNotChangeMembership`、`RemovedFriend_LatePresenceDoesNotComeBack` |
| C07 | 通过 | `FriendPages_MergeDuplicateIds_AndKeepOldListWhenALaterPageFails`、`RefreshRequests_KeepsPreviousListWhenAPageFails` |
| C08 | 通过 | `InFlightFriendRefresh_DoesNotWipeLocalUpsert`、`PushDuringFriendRefresh_RerunsAndKeepsServerMembership`、`PresenceDuringSnapshot_KeepsNewerOnlineState` |
| C09 | 通过 | `RefreshAfterDisconnect_ClearsStaleAndReplacesSnapshot`。实机重连未跑。世界快照失败仍拉好友没有单独 EditMode |
| C10 | 通过 | `SwitchCharacter_DropsPreviousSnapshot`、`Clear_DropsLateResponseFromPreviousGeneration` |
| C11 | 通过 | `ClearThenPush_DoesNotThrowOrRestoreOldFriend`。面板只在 `BuildOnce` 绑监听。未做切场景销毁的 PlayMode |
| C12 | 通过 | `RetryableFriendError_ShowsLaterHint_AndDoesNotAutoResend`。可重试错误只写「稍后重试」，不再套 2.5 秒冷却。协议里没有 `ERR_NAME_AMBIGUOUS`，没有新增这个码 |
| C13 | 通过 | `FormatLastOnline_RelativeBuckets_DoNotThrow`、`PresenceHold_KeepsLastStateUntilFriendListArrives` |
| C14 | BLOCKED | `run_friends_e2e.ps1` 退出码 2。`GAMEMESH_E2E_GATEWAY` 未设置。PlayMode NOT RUN |

`git diff --check` 无输出。

## 双账号手工步骤

环境变量 `GAMEMESH_E2E_GATEWAY=1`，然后运行 `Tools/GameMesh/run_friends_e2e.ps1`。脚本会起 A、B：

1. A 按 B 的 ID 搜索并申请。
2. B 看到申请并同意。
3. A 的好友列表出现 B。
4. B 断开再回到世界。A 应先看到离线，再由好友列表而不是补发的在线推送看到在线。
5. B 删除 A。A 看到好友消失。
6. A 拉黑 B。B 再申请时不应看到「对方拉黑了你」。
7. 两边登出，事件里有 `session_closed`。

没有这次实机日志，不能标通过。

## 本次修改

- `FriendClient.cs`：关系推送碰到正在进行的全量列表时，旧页不覆盖，并最多再拉两次。快照落地后合并更新的在线状态。
- `GameMeshClient.cs`：`need_full_snapshot` 仍只请求世界快照。世界快照失败时继续拉好友三份列表。
- `FriendClientTests.cs`：补了推送打断分页、以及快照期间在线推送的用例。
- 没有改场景、Prefab、`.meta` 或 `Game.cs`。

## 仍须用户处理

- 设置 Gateway 后跑双账号脚本。
- 私聊、修改备注、查看他人资料都还没有服务端写接口或现有界面，没有补假功能。
