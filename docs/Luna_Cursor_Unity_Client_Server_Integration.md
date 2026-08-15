# Luna Unity 客户端：GameMesh 服务器联调 Cursor 执行提示词

> 目标仓库：`https://github.com/madongxin/luna`
>
> 分支：`main`
>
> 本提示词依据代码基线：`2bec39c000a3c39635ee9e186552dcf7a2307569`
>
> Unity：`2022.3.62f3c1`
>
> 对接服务器：`https://github.com/madongxin/webserver`，外网只连接 Gateway TCP

请把本文完整交给 Unity 客户端仓库中的 Cursor。Cursor 必须直接修改项目、生成可运行 Demo 和自动化测试，不能只输出示例代码或架构建议。

## 1. 本批目标

在现有 FPS Microgame Demo 上实现 GameMesh 联调纵向切片：

1. 客户端与 Gateway 完成 TCP + protobuf 基础通信。
2. 提供注册、登录、主动下线和断线重连。
3. 展示服务器权威玩家 ID、名字和基础战斗属性。
4. 两个客户端进入同一公共地图，通过 AOI 看到彼此并同步移动。
5. 支持服务器“每地图最多 50 人、满员自动开新实例”的返回结果；客户端不自行选实例。
6. 玩家 A 向指定玩家 B 发送无附件普通邮件，B 能看到通知、列表和正文。
7. 提供 Unity 地图数据导出工具，将 MainScene 的边界、出生点和 walkable grid 提供给服务器加载。

本批不是完整 MMO 客户端。不要实现跨 Cell 无缝大世界、Addressables Chunk 流式加载、完整战斗/NPC/技能、预测回滚框架或商业级 UI 美术。

## 2. 当前项目事实

当前仓库是 Unity FPS Microgame：

- Unity 2022.3.62f3c1、URP 14.0.12。
- `Assets/FPS/Scenes/IntroMenu.unity` 和 `MainScene.unity` 已在 Build Settings。
- `MainScene` 已有 `NavMeshSurface`。
- `Assets/FPS/Prefabs/Player.prefab` 使用现有 `PlayerCharacterController`、`Health` 和第一人称 Camera。
- 项目尚无 TCP、protobuf、登录、Session、AOI 或邮件客户端代码。
- 项目已有 Unity Test Framework，但暂无本批网络测试。

因此必须新增独立 `Assets/GameMesh/` 模块，尽量通过适配器接入现有 FPS 代码；不要把网络逻辑散落到第三方 Microgame 源码，也不要重写现有移动控制器。

## 3. 开始前必须做的事

1. 如果仓库出现 `AGENTS.md`，完整阅读；同时阅读 Packages、ProjectSettings、Build Settings、现有 Scene/Prefab、玩家控制器和测试代码。
2. 执行并记录：

   ```bash
   git status --short
   git log -1 --oneline
   ```

3. 保留用户已有未提交修改；不回退、不覆盖、不删除无关资源。
4. 不手工编辑复杂 `.unity`、`.prefab` YAML。通过 Unity Editor、Prefab API 或可重复执行的 Editor 安装工具创建/修改资源，并确保 `.meta` 一起提交到工作区。
5. 先确认服务器已导出本批 `game.proto + descriptor + protocol manifest`。服务器协议是唯一事实源，客户端不得自己发明不同字段编号。
6. 每个阶段完成后运行对应 EditMode/PlayMode/BatchMode 测试并输出结果；上一阶段不通过，不进入下一阶段。
7. 未经用户明确要求，不 commit、不 tag、不 push。

## 4. 固定协议与安全边界

- Client 只连接 `Gateway VIP/host:game_port`，不连接 brpc、GameLogic、Session、World、GameDB、Redis、MySQL 或 etcd。
- TCP 帧：`4 字节 uint32 大端 payload 长度 + protobuf payload`。
- 请求 payload 是 `GameRequest`，响应/Push payload 是 `GameResponse`，单帧上限 4 MiB。
- 禁止使用 `BinaryFormatter`、JSON 替代业务 protobuf或手写 protobuf wire decoder。
- 服务器返回的 `player_id/session_id/token/generation/map_instance_id/owner_epoch/route_version` 是权威值。
- 不把密码、完整 token、reconnect ticket 写入日志、PlayerPrefs 或明文配置。
- Unity 对象只能在主线程访问；Socket 接收线程只能解析字节并投递 DTO/事件到主线程队列。
- 客户端不得发送现有管理型 `MailDeliver`、`GrantItem` 或任何正式模式禁止命令。

## 5. 建议目录

根据实际仓库微调，但保持清晰边界：

```text
Assets/GameMesh/
  Runtime/
    Bootstrap/
    Network/
    Protocol/
    Auth/
    Player/
    Map/
    Aoi/
    Mail/
    UI/
  Protocol/
    Schema/
    Generated/
    protocol_manifest.json
  Editor/
    MapExport/
    DemoInstaller/
  Prefabs/
  Scenes/
  Tests/EditMode/
  Tests/PlayMode/
Tools/GameMesh/
docs/
```

建立 Runtime、Editor、Tests 的 asmdef，Editor-only API 不得进入运行时程序集。

---

# 阶段 C1：协议生成、TCP SDK 与注册登录生命周期

## C1.1 导入并锁定服务器协议

新增跨平台工具：

```text
Tools/GameMesh/import_server_contract.sh
Tools/GameMesh/import_server_contract.ps1
Tools/GameMesh/generate_csharp_proto.sh
Tools/GameMesh/generate_csharp_proto.ps1
```

要求：

1. `import_server_contract` 接收服务器导出目录或服务器仓库路径参数，不能写死 `../webserver`。
2. 校验 `protocol_manifest.json`、schema SHA-256、frame format 和最大帧长度。
3. 将 `game.proto` 放入 `Assets/GameMesh/Protocol/Schema/`，用固定版本 `protoc` 生成 C# 到 `Generated/`。
4. C# namespace 必须是 `GameMesh.Protocol`；不得手工修改 generated 文件。
5. 引入与生成器兼容、版本固定的官方 `Google.Protobuf` C# runtime。可以用可重复恢复的 UPM/NuGet 方案或受控 DLL，但必须提交版本清单、许可证和恢复说明，不能依赖开发者机器偶然存在的 DLL。
6. 新增协议漂移检查：schema 或 manifest 变化但未重新生成 C# 时测试/CI 失败。
7. 生成脚本必须支持 Windows PowerShell；Shell 版本供 CI/Unix 使用。

服务器本批协议至少包含：

- Register/Login/Logout/Reconnect/PushAck。
- PlayerAttributes、Vec3、EntitySnapshot。
- EnterMap/LeaveMap/Move、AoiDelta。
- PlayerMailSend、MailboxSummary/MailList/MailGet、MailboxChangedNotify。
- ServerPushEnvelope；其 payload 是内层 `GameResponse`。

如果服务器协议尚未包含这些类型，停止本阶段并列出缺失契约；不要在 Unity 仓库单方面定义同名但字段不同的 proto。

## C1.2 TCP 网络层

实现可测试、与 Unity 生命周期解耦的网络模块，建议接口：

```csharp
public interface IGameConnection : IAsyncDisposable
{
    ConnectionState State { get; }
    Task ConnectAsync(string host, int port, CancellationToken ct);
    Task<GameResponse> RequestAsync(GameRequest request, TimeSpan timeout, CancellationToken ct);
    Task DisconnectAsync(DisconnectReason reason, CancellationToken ct);
    event Action<GameResponse> PushReceived;
    event Action<ConnectionState> StateChanged;
}
```

必须正确处理：

1. DNS/IP、连接超时、取消、重复 Connect 和关闭竞态。
2. `SendAsync` 可能部分写，循环发送完整帧。
3. 接收半包、粘包和多帧；长度按大端读取。
4. 长度 0、超过 4 MiB、protobuf 解析失败时 fail-closed 并断开。
5. 单一发送队列保证多个请求的帧不交叉；队列有上限。
6. 外层 `GameRequest.seq` 使用进程生命周期单调递增 `ulong`；不能因重连回绕或重置造成重复业务请求。
7. `pending[seq]` 关联响应；响应可乱序，超时/取消/断开时完整释放 TaskCompletionSource。
8. `GameResponse.seq==0` 或 `server_push` 走 Push 分发，不误完成普通请求。
9. Socket/解析运行在后台；事件通过 `GameMeshMainThreadDispatcher` 在 Unity 主线程派发。
10. 停止时取消 receive loop、关闭 socket、等待任务退出，不使用 `Thread.Abort` 或遗留后台线程。
11. 指标/日志包含连接状态、seq、消息类型和错误码，但敏感凭证脱敏。

连接状态机：

```text
Disconnected → Connecting → Connected → Authenticating
→ Authenticated → EnteringWorld → InWorld
→ Reconnecting 或 Closing → Disconnected
```

非法状态调用必须返回明确客户端错误，不能 NullReference 或静默吞掉。

## C1.3 Session 和可靠 Push

实现 `GameSession`，内存保存：

```text
player_id
session_id
fence_token/token
generation
last_server_seq
map_template_id
map_instance_id
owner_epoch
route_version
```

规则：

- 密码只在 Register/Login 请求瞬时使用；完成后清空 UI 输入和临时变量。
- token/reconnect ticket 默认只在内存保存；本批不做免密长期登录。
- 收到可靠 `ServerPushEnvelope` 时按 `server_seq` 去重和检测缺口。
- 先解析并在主线程成功应用内层 `GameResponse`，再发送 PushAck。
- 重复 Push 不重复创建实体或重复弹邮件；仍可重发当前 ACK。
- 断线自动进入 Reconnecting，指数退避加随机抖动，最大次数/总时长配置化。
- Reconnect 携带 `player_id/session_id/reconnect_ticket/last_server_seq`。
- Reconnect 成功后原子替换新 token/generation，再处理 replay 或 full snapshot。
- 用户主动 Logout 时关闭自动重连，等待 Logout 响应或短超时后关闭 TCP，并清空敏感 Session。

## C1.4 注册、登录与玩家属性 UI

在 IntroMenu 增加可运行的联调面板，至少包含：

- Server host、game port。
- device ID、display name、password。
- Register、Login 按钮。
- player ID 输入/显示。
- 连接状态和非敏感错误信息。

注册成功后显示服务器分配的 player ID；登录成功后进入 MainScene，并展示服务器权威属性：

```text
玩家 ID、名字、HP/MaxHP、MP/MaxMP、攻击、法强、
防御、魔抗、暴击率、暴击伤害、移动速度、攻击速度
```

适配现有 FPS 组件：

- 将服务器 `max_hp/hp` 映射到现有 `Health.MaxHealth/CurrentHealth`。
- 将服务器 `move_speed` 映射到 `PlayerCharacterController.MaxSpeedOnGround`；冲刺策略必须与服务器速度校验一致或在联调模式禁用冲刺。
- 属性仅用于显示和本地表现，客户端不能写回服务器权威值。
- 网络模块通过持久化 Bootstrap 对象跨 Scene 存活；不要创建多个 Socket 单例。

## C1.5 C1 测试

EditMode 测试至少覆盖：

1. 大端长度编码。
2. 半包、粘包、连续多帧解析。
3. 长度 0、超 4 MiB、坏 protobuf 被拒绝。
4. 并发请求响应乱序仍按 seq 正确完成。
5. 超时/取消/断开清理 pending。
6. Push 与普通响应分流；内层 GameResponse 正确解析。
7. server_seq 去重、缺口、应用后 ACK。
8. ConnectionState 合法/非法转换。
9. 协议 hash 漂移检查。

PlayMode 测试至少使用本地 fake TCP server 验证真实 Socket 半包/粘包、断开和重连；不得只 Mock 掉 framing。

C1 完成后输出修改文件、依赖版本、生成命令和真实测试结果。通过后才进入 C2。

---

# 阶段 C2：地图导出、进入公共地图、移动与 AOI

## C2.1 Unity 地图导出工具

在 Editor 程序集中实现 `GameMeshMapExporter`，通过菜单和 BatchMode 均可运行，例如：

```text
GameMesh/Map/Export Current Scene
-executeMethod GameMesh.Editor.MapExportBatch.ExportMainScene
```

导出 `MapStaticData V1`，字段必须与服务器 `docs/map-data-v1.md` 完全一致：

```text
schema_version
map_template_id（MainScene 默认 1001，可在配置资产修改）
scene_name
data_version
bounds_min / bounds_max
aoi_cell_size
nav_sample_step
grid_width / grid_height
walkable_rle
spawn_points
```

同时输出原始 JSON 的 `.sha256`。

实现规则：

1. 水平面使用 Unity X/Z，Y 是高度，不交换坐标轴。
2. 优先使用 `NavMesh.CalculateTriangulation()` 顶点范围确定可行走区域 bounds，避免灯光、天空盒或远处特效扩大范围。
3. 使用固定 `nav_sample_step` 和 `NavMesh.SamplePosition` 生成 walkable bit grid；采样顺序、RLE 和浮点格式必须确定性。
4. AOI cell size 与 walkable 采样步长分别配置，不能混为一个参数。
5. 使用 `GameMeshSpawnPoint` 组件标注出生点；没有标记时可迁移当前 Player 起点为默认出生点，但最终导出至少一个点。
6. 出生点越界或不可走、空 NavMesh、非法 bounds、重复 map_template_id 时导出失败。
7. 同一 Scene 未变化时重复导出字节和 hash 完全一致。
8. 默认输出到项目内受控目录，并提供“复制到服务器目标目录”的显式命令参数；不得写死用户磁盘路径。
9. 不向服务器导出 Scene、Prefab、FBX 或 Unity NavMesh 二进制。

## C2.2 进入公共地图

登录成功并加载 MainScene 后：

1. 客户端发送 `EnterMap`：`realm_id=1, map_template_id=1001, map_instance_id=0`，并携带本地地图 data version/hash。
2. 客户端不能选择 GameLogic；也不能为了和朋友同服而猜测 map_instance_id。
3. 使用服务器返回的 `map_instance_id/owner_epoch/route_version` 更新 Session。
4. 使用服务器确认出生点放置本地 Player。
5. 校验服务器地图 hash；不一致显示明确阻塞提示，不进入游戏。
6. 显示当前 `map_instance_id` 和当前人数，便于验证 50 人开新实例。
7. 场景切换、重复 EnterMap、应用退出时不得留下重复实体或重复网络订阅。

## C2.3 本地移动上报与纠偏

不重写 `PlayerCharacterController`。新增适配器观察本地玩家 Transform：

- 默认 10 Hz 上报位置/yaw；只有位移或旋转超过阈值才发。
- 使用外层递增 seq；一次只保留有限数量在途 Move。
- 服务器响应是权威位置。
- 小误差平滑收敛，大误差或服务器拒绝时立即/快速拉回。
- 防止纠偏再次触发无限上报。
- 离开地图、断线或未登录时停止上报。
- NaN/Inf 坐标在客户端先拒绝，但仍依赖服务器校验。

现有 FPS 冲刺速度可能是基础速度两倍；必须读取服务器契约：如果服务器没有 Sprint 输入和对应校验，本批联调模式禁用 Sprint，不能频繁触发超速拒绝。

## C2.4 AOI 与远端玩家表现

实现 `AoiWorld` 和 `RemotePlayerView`：

1. EnterMap 初始快照创建附近远端玩家。
2. `AoiDelta ENTER` 幂等创建；已存在则刷新。
3. `MOVE` 按实体 `state_seq` 丢弃旧/重复状态，使用带缓冲的插值，禁止瞬移抖动；大距离允许 snap。
4. `LEAVE` 幂等销毁；场景卸载/断线/重新进入时清空。
5. 绝不为自己的 player ID 创建远端实体。
6. 不同 `map_instance_id` 的消息拒绝并记录协议错误。
7. Remote prefab 不包含本地输入、主 Camera、AudioListener 或本地 Weapon 控制；首期可使用 Capsule/简化模型加名字和 HP 条。
8. 所有 Instantiate/Transform/UI 操作在 Unity 主线程。
9. 高频 Move 可合并，但 Enter/Leave 不可被旧 Move 覆盖。
10. 重连成功后以服务器 AOI 全量快照重建，不能把旧 Gateway 前的远端对象盲目保留。

## C2.5 C2 测试

EditMode：

1. Map exporter 相同输入输出相同 hash。
2. X/Z grid、边界 cell 和 RLE 可由测试 decoder 还原。
3. 缺 NavMesh、缺出生点、非法点导出失败。
4. AOI Enter/Move/Leave 幂等。
5. 旧 state_seq 不回退远端位置。
6. 不同 map_instance 的事件不污染当前世界。
7. 本地 Move 采样频率和纠偏阈值正确。

PlayMode：

- Fake server 返回自己 + 一个远端实体，验证创建、移动插值、离开销毁和重连重建。
- 断线/Scene reload 后没有重复 Socket、重复事件订阅或残留 Remote GameObject。

C2 通过后才进入 C3。

---

# 阶段 C3：玩家邮件 UI、真实双客户端 E2E 与可交付 Demo

## C3.1 邮件 UI

增加最小邮箱面板：

- 未读数/邮箱摘要。
- 邮件列表。
- 标题、发件人、时间、正文详情。
- 刷新按钮。
- 发邮件面板：接收者 player ID、标题、正文、发送按钮。

规则：

1. 发送使用新的 `PlayerMailSendReq`，不允许附件。
2. sender player ID/name 不从输入框获取，由 Session/服务器确定。
3. 客户端做长度和空值提示，但服务器仍是最终校验者。
4. 每次发送生成稳定 operation ID；网络超时重试复用同一 ID。
5. 收到 `mailbox.changed.v1` 后去抖刷新摘要/列表。
6. 即使 Push 丢失，也每 10 秒或打开面板时轮询一次作为兜底；避免每帧查询。
7. MailList/MailGet 乱序响应不能让旧页面覆盖新页面。
8. 登出后清空邮件 UI 和缓存，不泄露上个账号数据。

## C3.2 Demo 安装和配置

提供一个可重复执行的 Editor 工具，把联调组件安装到 IntroMenu、MainScene 或单独 GameMesh Demo Scene。不要要求用户手工拖几十个引用。

提供非敏感配置 `GameMeshClientConfig`：

```text
host
port
connect_timeout_ms
request_timeout_ms
reconnect_max_attempts
move_send_hz
interpolation_delay_ms
map_template_id
map_data_version/hash
```

要求：

- 默认 `127.0.0.1` 仅用于开发；Build 可用命令行覆盖。
- 密码和 Token 不进入 ScriptableObject。
- 支持命令行：

  ```text
  -gamemeshHost
  -gamemeshPort
  -gamemeshDevice
  -gamemeshPassword
  -gamemeshAutoScenario
  -gamemeshPeerPlayerId
  ```

- 提供开发面板显示连接状态、player ID、map instance、last client seq、last server seq 和最后错误码，但不显示完整 token。

## C3.3 Unity 自动化和真实服务器 E2E

新增：

```text
Tools/GameMesh/run_editmode_tests.sh/.ps1
Tools/GameMesh/run_playmode_tests.sh/.ps1
Tools/GameMesh/build_integration_client.sh/.ps1
Tools/GameMesh/run_two_clients_e2e.sh/.ps1
docs/GameMesh_Unity_Integration.md
```

脚本要求：

- Unity 可执行路径通过参数或 `UNITY_PATH` 提供，不写死个人路径。
- BatchMode 日志和 XML 测试结果落到 `Logs/`/`TestResults/`，失败返回非零。
- 两客户端 E2E 使用两个独立进程和不同 device ID；不共享 PlayerPrefs 目录或 Session 文件。
- 超时、没有收到指定 Push、字段不一致时失败，不能只看到客户端进程启动就算成功。

## C3.4 最终双客户端场景

在真实 GameMesh 集群上执行：

1. Client A、B 分别连 Gateway 公网 TCP。
2. A、B 各自注册、登录，得到不同 player ID 和完整玩家属性。
3. 两端进入 `map_template_id=1001, map_instance_id=0`。
4. 断言服务器返回相同 `map_instance_id`；调试面板记录相同 Owner（如果协议对客户端公开）。
5. A 和 B 均创建对方 RemotePlayer。
6. A 移动后，B 在限定时间内看到 A 平滑移动到服务器确认位置；反向也测试一次。
7. A 给 B 的 player ID 发邮件；B 收到通知或轮询发现，打开详情后标题/正文一致。
8. A 主动 Logout；B 收到 AOI Leave；A 不自动重连。
9. B 非主动断网后重新连接另一个 Gateway，恢复同一地图且没有重复 RemotePlayer。
10. 地图 hash 不一致时客户端明确阻止进图并显示期望版本。

另外使用服务器脚本验证 51 人容量；Unity 不需要同时启动 51 个渲染客户端，但至少用 2 个 Unity 客户端验证它们对服务器返回的不同 map instance 能正确隔离。

## C3.5 最终验收标准

- Unity Console 无本批相关 Exception、线程访问 Unity API 错误或未释放 Socket 警告。
- EditMode、PlayMode 和协议漂移检查全部通过。
- Windows 或当前目标平台构建成功。
- 两个真实客户端完成登录、同图可见、移动、邮件、Logout 和一次断线重连。
- 打包客户端只知道 Gateway 地址，不包含 Redis/MySQL/brpc/内部服务地址。
- 协议和地图 hash 与服务器 release 文档一致。

## 6. Cursor 最终输出格式

每个阶段结束时输出：

1. 当前 commit、工作区状态和修改文件/资源清单。
2. 新增依赖及固定版本。
3. 协议导入、C# 生成和地图导出命令。
4. 场景/Prefab 自动安装方法。
5. 实际执行的 EditMode、PlayMode、Build 和 E2E 命令及退出码。
6. 未执行项和缺失环境，禁止把未执行写成通过。
7. 与服务器对应的 protocol hash、map hash 和版本。
8. 已知限制与回滚方式。

只有真实双客户端 E2E 通过后，才可把本批 Unity 联调功能标记为完成。
