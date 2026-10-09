# 好友系统客户端验收

结论：**部分通过**。EditMode 97/97 通过。双账号实机流程没有 Gateway，不能记为通过。

## 1. 现状

| 功能 | 位置 | 改前 | 这次 |
| --- | --- | --- | --- |
| 好友请求与状态 | `FriendClient` | 已有搜索、申请、同意、拒绝、删除、拉黑、解除、推送去重、超时重试、会话隔离 | 好友列表和黑名单改为整份快照成功后才替换；按 id 去重；支持再拉一页 |
| 申请列表 | `FriendClient.ReadRequestsAsync` | 已按游标攒齐再替换 | 跨页按 `request_id` 去重；未拉完不把角标当成总数 |
| 好友界面 | `FriendScreen` | 列表、申请、黑名单、搜索、二次确认 | 刷新、加载更多；过期申请不再给同意/拒绝；等级为 0 时显示「等级未知」 |
| 登录与重连 | `RefreshAfterLoginAsync` | 拉好友和申请 | 同时拉黑名单 |
| 错误文案 | `GameErrorCatalog` | 好友错误码已有中文 | `ERR_DEPENDENCY_UNAVAILABLE` 改为「服务暂不可用」，不再写出内部服务名 |
| 推送确认 | `GameMeshClient` 对 `PushAckReq` | 已有，好友推送走同一条推送路径 | 未另做一套 |
| 私聊 | 无界面 | 不发送 | 仍不新增 |
| 备注编辑 | 协议只有 `FriendBrief.remark` 只读 | 只展示 | 仍不能保存 |

## 2. 修改文件

- `Assets/GameMesh/Runtime/Friends/FriendClient.cs`：`ReadFriendsAsync`、`ReadRequestsAsync`、`ReadBlockedAsync`、`LoadMore*Async`、`OpenFriendPageAsync`、`IsRequestExpired`。`operation_id` 最长 96 字符。
- `Assets/GameMesh/Runtime/UI/FriendScreen.cs`：每个列表有刷新和加载更多；过期申请只显示状态。
- `Assets/GameMesh/Runtime/Protocol/GameErrorCatalog.cs`：依赖不可用的玩家文案。
- `Assets/GameMesh/Tests/EditMode/FriendClientTests.cs`：分页、去重、失败保留旧列表、加载更多、过期申请、空列表、等级缺省、`operation_id` 长度。

## 3. 协议对照

请求都由 `FriendClient` 放进 `GameRequest`，经现有 `GameConnection` 发出。`seq` 仍由连接层分配，不当作 `operation_id`。

| 业务 | 请求 | 处理 |
| --- | --- | --- |
| 好友列表 | `FriendListReq` | `ReadFriendsAsync`，`page_size` 50，读到 `next_cursor` 为空才替换 |
| 搜索 | `FriendSearchReq` | `SearchAsync`，ID 或精确名字 |
| 申请 | `FriendApplyReq` | `ApplyAsync`，同一目标复用 `operation_id` |
| 申请列表 | `FriendRequestListReq` | `ReadRequestsAsync`，`page_size` 20 |
| 同意 / 拒绝 | `FriendAcceptReq` / `FriendRejectReq` | 本地已过期则不发送 |
| 删除 / 拉黑 / 解除 | 对应 Req | `DeleteAsync` / `BlockAsync` / `UnblockAsync` |
| 黑名单 | `FriendBlockListReq` | `ReadBlockedAsync` |
| 推送 | `FriendRequestPush`、`FriendAddedPush`、`FriendRemovedPush`、`FriendPresencePush` | `ApplyPush`。在线状态只更新已有好友 |

## 4. 测试

命令：

`Unity.exe -batchmode -nographics -runTests -testPlatform EditMode -testResults Logs/editmode-friends5.xml`

结果：`total=97`，`passed=97`，`failed=0`。时间 `2026-10-09 04:13:28Z`。

`Tools/GameMesh/run_friends_e2e.ps1`：退出码 **2**，输出 `BLOCKED: GAMEMESH_E2E_GATEWAY is not set`。这不是失败，也不是通过。

未跑 PlayMode。未做真 Gateway 双账号点击。

## 5. 双账号验收

未执行。本机没有 `GAMEMESH_E2E_GATEWAY`。脚本在环境齐备时会走：搜索、申请、同意、好友列表、掉线再上线、删除、拉黑。不能把未跑的步骤写成已通过。

## 6. 剩余阻塞

- 真 Gateway 双账号，以及重连后列表恢复在线，都还没在实机上跑。
- 没有私聊界面。服务端拒绝私聊时，客户端只能在以后有入口时显示「已拉黑，解除后可私聊」。不能声称私聊安全。
- 没有修改备注的请求。备注只展示服务端带回的 `remark`。
- 没有其他玩家的资料页，只有 `GetSelfProfile`。好友行展示名字、ID、等级、职业和在线时间，不另做资料窗口。
- `avatar` 没有远程图片加载，空头像不显示假图。

## 7. 结论

**部分通过。** 分页、失败不覆盖、过期申请和错误提示有 EditMode 证据。实机好友闭环没有证据。
