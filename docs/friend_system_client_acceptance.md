# 好友系统客户端验收

本地 HEAD（改动前）：`be94fa3`。Unity `2022.3.62f3c1`。这些改动还没提交。

结论：**部分通过**。EditMode 102/102 通过。双账号实机没有跑。

## 功能映射

| 功能 | 已有位置 | 协议 | 这次 |
| --- | --- | --- | --- |
| 入口和面板 | `FriendScreen.Ensure`，运行时创建，右上角「好友」 | 不直接发协议 | 断线或重连保持期间，状态行显示「在线状态可能不是最新」 |
| 好友 / 申请 / 黑名单 | `FriendClient.ReadFriendsAsync` 等 | `FriendList`、`FriendRequestList`、`FriendBlockList` | 同一种全量刷新在进行时再点一次，不会并行再开一轮，结束后再补拉一次 |
| 搜索和申请 | `SearchAsync`、`ApplyAsync` | `FriendSearch`、`FriendApply` | 一次点击结束后释放 `operation_id`；超时重试仍复用；下一次点击用新 id |
| 同意 / 拒绝 / 删除 / 拉黑 | 对应 `*Async` | 对应 Req，带 `operation_id` | 业务结果返回后同样释放 id |
| 推送 | `ApplyPush` | 四种好友 push | 删除之后迟到的在线推送不会把好友加回来 |
| 登录和重连 | `GameMeshClient` 调 `RefreshAfterLoginAsync` | 三份列表 | 重连开始 `BeginPresenceHold`，数据标成可能过期；快照成功后清掉 |
| 切角色 | `Friends.Clear` 增加会话代次 | 迟到回包按代次丢弃 | 补了 A 的回包不能写成 B 的列表 |
| 推送确认 | `GameMeshClient` 的 `PushAckReq` | `push_ack` | 未改 |
| 私聊 / 备注 / 资料页 | 无私聊界面，无备注写接口，无他人资料页 | 无 | 未编造 |

面板不是 Prefab。`BuildOnce` 只绑一次按钮。没有新增场景或 `.meta`。

## 同步顺序

1. 登录或重连成功后 `RefreshAfterLoginAsync`：好友、申请、黑名单都从空游标拉到尾，再整表替换。
2. 某一页失败：不替换已有快照。
3. 同步过程中又来一次全量刷新：记下再跑一轮，不并行开多轮。
4. 同步过程中的关系推送会推进列表代次，旧快照作废，本地修改留下。
5. 断线：`DataStale`。界面不把这份在线状态说成实时。
6. 登出或被挤下线：`Clear`，代次加一，列表和角标清空。

## 测试

命令：

`Unity.exe -batchmode -nographics -projectPath C:\Users\dongx\FirstFPS -runTests -testPlatform EditMode -testResults Logs/editmode-friends6.xml`

结果：`total=102`，`passed=102`，`failed=0`。时间 `2026-10-09 04:35:16Z`。

| ID | 结果 | 证据 |
| --- | --- | --- |
| C01 | 通过 | `EmptyFriendPage_DoesNotInventFriends`、`FriendPages_MergeDuplicateIds_AndKeepOldListWhenALaterPageFails`、`LoadMoreFriends_AppendsNextPageWithoutDroppingTheFirst` |
| C02 | 通过 | `Search_DebouncesAndAcceptsOddCharacters` |
| C03 | 通过 | `Apply_CompletedClickUsesNewOperationId`、`TransportTimeout_RetriesOnceWithSameOperationId` |
| C04 | 通过 | `Accept_InsertsPeerAndClearsRequest`、`ExpiredRequest_DoesNotSendAccept` |
| C05 | 通过 | `Block_RemovesFriendAndRequests_UnblockReversesSearchRelation` |
| C06 | 通过 | `Pushes_RemoveAddAndDedupeRequests_PresenceDoesNotChangeMembership`、`RemovedFriend_LatePresenceDoesNotComeBack` |
| C07 | 通过 | `FriendPages_MergeDuplicateIds_AndKeepOldListWhenALaterPageFails`、`RefreshRequests_KeepsPreviousListWhenAPageFails` |
| C08 | 通过 | `InFlightFriendRefresh_DoesNotWipeLocalUpsert` |
| C09 | 通过 | `RefreshAfterDisconnect_ClearsStaleAndReplacesSnapshot`。实机重连未跑 |
| C10 | 通过 | `SwitchCharacter_DropsPreviousSnapshot`、`Clear_DropsLateResponseFromPreviousGeneration` |
| C11 | 通过 | `ClearThenPush_DoesNotThrowOrRestoreOldFriend`。面板只在 `BuildOnce` 绑监听。未做切场景销毁的 PlayMode |
| C12 | 通过 | `RetryableFriendError_ShowsLaterHint_AndDoesNotAutoResend`、`TransportTimeout_BothFailuresDoNotWrite`。`ERR_DEPENDENCY_UNAVAILABLE` 文案是「服务暂不可用」 |
| C13 | 通过 | `FormatLastOnline_RelativeBuckets_DoNotThrow`、`PresenceHold_KeepsLastStateUntilFriendListArrives` |
| C14 | 未执行 | `run_friends_e2e.ps1` 需要 `GAMEMESH_E2E_GATEWAY`。本次没有重跑脚本 |

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

## 仍须用户处理

- 设置 Gateway 后跑双账号脚本。
- 私聊、修改备注、查看他人资料都还没有服务端写接口或现有界面，没有补假功能。
- 这些改动还在工作区，没有提交。
