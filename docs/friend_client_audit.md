# 好友客户端盘点

日期：2026-10-12。Unity `2022.3.62f3c1`。

本地与远端 `origin/main` 都是 `14460190a1a43bebcd69003b1ae89dea028a19ce`。`git fetch --prune origin` 成功，没有新提交，工作区当时是干净的，没有做 reset。配套 `webserver` 仓库不在本工作区，协议以 `Assets/GameMesh/Protocol/Schema/game.proto` 与已生成的 `Game.cs` 为准。没有手改生成物。

没有名叫 `FriendManager` 的类型。好友逻辑在 `FriendClient`，可玩界面是运行时创建的 uGUI `FriendScreen`，不是 Prefab。调试开关 `imguiFriendDebug` 打开时改走 `GameMeshRuntimeUi` 的 IMGUI。

## 功能矩阵

| 功能 | 状态 | 证据 |
| --- | --- | --- |
| 好友 / 申请 / 黑名单列表 | 已完整 | `FriendClient.ReadFriendsAsync`、`ReadRequestsAsync`、`ReadBlockedAsync`。游标原样传递。空页仍带 `next_cursor`、重复游标、后页失败都不替换旧快照 |
| 数量与上限 | 已完整 | `FriendCount` / `FriendCap`，界面 `FriendScreen` 的 `_count` |
| 等级 0、缺头像 | 已完整 | `FormatFriendLine`、`SearchLine` 把等级 0 显示成「等级未知」。没有远程头像加载，不画假头像 |
| 搜索与关系文案 | 已完整 | `SearchAsync`、`SearchRelationLabel`。没有「对方拉黑了你」 |
| 申请发送中 | 本次修复 | `ApplyCoreAsync` 在发出请求前把 `LastNotice` 设为「发送中」，成功后换成「已提交」或「已发送好友申请」 |
| 同意 / 拒绝 / 过期 | 已完整 | `AcceptAsync`、`RejectAsync`。`ExpireAt == 0` 不当作过期。过期申请不发请求 |
| 删除 / 拉黑确认 | 已完整 | `FriendScreen.BeginConfirm`。失败时 `CommitAsync` 返回，不调用 `RemoveFriend` |
| 解除拉黑确认 | 本次修复 | 面板与 IMGUI 都要再确认一次才调用 `UnblockAsync`。成功后只从黑名单移除，不把对方加回好友 |
| 在线推送乱序 | 本次修复 | `ApplyPresence`。已有 `last_online_time` 时，更早或为 0 的推送不改在线状态。协议没有 presence 序号 |
| 登录与重连快照 | 已完整 | `GameMeshClient` 在登录、进图、重连成功后调用 `RefreshAfterLoginAsync`，三份列表都拉完。重连开始 `BeginPresenceHold`，`DataStale` 为真 |
| 换角 / 被踢 | 已完整 | `Friends.Clear` 增加 `_sessionGeneration`。旧回包代次不符则丢弃 |
| 全量刷新互斥 | 已完整 | `_friendRefreshTask` 等。进行中的全量刷新只补跑一轮 |
| 操作 ID | 已完整 | `StableOp`。超时重试复用同一次请求对象。业务结果返回后 `ForgetOp`，下一次点击用新 id |
| 推送 ACK | 已完整 | `GameMeshClient.OnPush`。可靠推送在应用之后 ACK。`friend.presence.v1` 解析后返回，不走这条 ACK |
| 红点 | 部分实现 | `RequestBadge` 来自当前申请集合。还有下一页时显示「N+」。不是服务器单独的红点字段 |
| 私聊 | 缺失，需服务端入口 | 协议有 `ChatSendReq`，好友面板没有私聊按钮。没有本地假发送 |
| 修改备注 | 需服务端 | `remark` 只展示。没有写备注的请求 |
| 他人资料 / 头像 | 需服务端 | 只有 `GetSelfProfile`。`avatar` 没有图片加载 |
| 双账号实机 | 无法测试 | `Tools/GameMesh/run_friends_e2e.ps1` 在未设置 `GAMEMESH_E2E_GATEWAY` 时退出码 2 |

## 请求对照

全部走 Gateway 上的 `GameRequest` / `GameResponse`，身份用当前 `GameSession`，不把搜索框里的 id 当成自己的 `player_id`。

| 动作 | 请求 | 推送 |
| --- | --- | --- |
| 好友列表 | `FriendListReq` | — |
| 搜索 | `FriendSearchReq` | — |
| 申请 | `FriendApplyReq.operation_id` | `FriendRequestPush` |
| 申请列表 | `FriendRequestListReq` | — |
| 同意 / 拒绝 | `FriendAcceptReq` / `FriendRejectReq` | `FriendAddedPush` |
| 删除 | `FriendDeleteReq` | `FriendRemovedPush` |
| 拉黑 / 解除 / 黑名单 | `FriendBlockReq` / `FriendUnblockReq` / `FriendBlockListReq` | — |
| 在线 | 列表里的 `online`、`last_online_time` | `FriendPresencePush` |

`last_online_time` 在 proto 里是 `uint64`，没有单位注释。客户端按 Unix 秒显示。没有毫秒猜测。

## 本次改动

- `Assets/GameMesh/Runtime/Friends/FriendClient.cs`
- `Assets/GameMesh/Runtime/UI/FriendScreen.cs`
- `Assets/GameMesh/Runtime/UI/GameMeshRuntimeUi.cs`
- `Assets/GameMesh/Tests/EditMode/FriendClientTests.cs`
- `docs/friend_client_audit.md`

没有改场景、Prefab、`.meta` 或 `Game.cs`。

## 测试

EditMode：

`Unity.exe -batchmode -nographics -projectPath C:\Users\dongx\FirstFPS -runTests -testPlatform EditMode -testResults Logs/editmode-friends8.xml`

`Logs/editmode-friends8.xml`：`total=108`，`passed=108`，`failed=0`。时间 `2026-10-12 02:01:33Z`。

本次新增并通过：`Apply_ShowsSendingUntilTheResponseArrives`、`StalePresence_DoesNotRollBackLastOnline`、`UnblockConfirm_UsesDistinctCopy`。

PlayMode：NOT RUN。

双账号：`run_friends_e2e.ps1` 退出码 2，BLOCKED。环境变量 `GAMEMESH_E2E_GATEWAY` 未设置。

PlayMode 和双账号实机仍未跑。
