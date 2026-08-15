# Luna Unity 客户端基础能力完善：Cursor 分阶段执行提示词

> 目标仓库：`https://github.com/madongxin/luna`  
> 目标分支：`main`  
> 本次审计基线：`084fcd4cc133974b390c4e234b236cf34cd931d2`  
> 协议事实源：`https://github.com/madongxin/webserver` 的 `proto/game.proto`  
> 本次服务器审计基线：`145a64753aacd0d9e1dc7916edee81d15f183148`

> 远端复核：2026-08-15 再次执行 `git pull --ff-only origin main` 和
> `git ls-remote origin refs/heads/main`，客户端远端仍为 `084fcd4`，没有发现用户所说的
> 后续提交；服务器远端仍为 `145a647`。如果执行时已出现新 HEAD，Cursor 必须先逐项确认
> 缺口是否已经修复，再决定修改范围，禁止重复实现。

请把本文件整体交给 Cursor。Cursor 必须按 C0 → C1 → C2 → C3 顺序执行；前一阶段未通过时不能继续下一阶段。

## 1. 最终目标

把当前 Unity Demo 从“已有网络代码和联调 UI”完善为真正能与 GameMesh 服务器联调的基础客户端：

- 从服务器唯一协议事实源生成 C#，杜绝协议分叉。
- 真实完成注册、登录、资料加载、进图、移动、AOI、邮件和 Logout。
- 断线后能重连原会话、恢复原地图和 AOI，不盲目申请新地图。
- 处理协议版本、心跳、统一错误码、Push 序号缺口和世界快照。
- 两个真实 Unity 进程的 E2E 有自动断言和可靠退出码。
- 密码/Token 不落日志和普通本地配置，所有 Unity 对象只在主线程访问。

## 2. 已确认的现状与缺口

当前版本已经有：

- `GameConnection`、4 字节大端长度帧、请求 seq/pending、收发循环。
- 注册、登录、Logout、Reconnect、EnterMap、邮箱查询 UI。
- `AoiWorld`、远端 Capsule、插值、MoveSampler、PushReliability。
- 地图导出文件，且当前地图 JSON 与服务器 hash 一致。
- EditMode/PlayMode 测试脚本和双客户端启动脚本框架。

但当前代码不能视为真实联调完成：

1. 客户端 `Assets/GameMesh/Protocol/Schema/game.proto` 的 hash 是旧值，manifest 指向服务器旧提交 `37b1977...`；服务器最新 schema 已增加 PlayerAttributes、Vec3、EntitySnapshot、MoveReq、AoiDelta、PlayerMailSendReq、MailboxChangedNotify。
2. `Generated/Game.cs` 仍由旧 schema 生成，新的协议类型在客户端不可用。
3. `Protocol_ReportsMissingUnitySliceTypes` 测试反而断言这些类型必须缺失，目标已经过时。
4. `GameMeshWorldBinder.MaybeReportMove()` 只调用 `MarkSent()`，没有构造并发送 `MoveReq`。
5. `ApplyInnerPush()` 没有把 `AoiDelta` 映射到 `AoiWorld`，所以远端玩家不会由真实 Push 创建/移动/销毁。
6. `MailClient.SendAsync()` 只返回“mapping 未编译”的文本，没有发送 `PlayerMailSendReq`。
7. `MailboxChangedNotify` 未被处理；邮箱主要靠固定 10 秒轮询。
8. EnterMap 没发送真实 `map_data_version/map_data_sha256`，hash 校验只是检查本地字符串是否以 `FORCE_MISMATCH` 开头。
9. 登录响应中的服务器权威 PlayerAttributes 未真正应用到 Session。
10. Push 序号出现 gap 时只打日志，没有请求 AOI/世界快照。
11. Reconnect 后如果已有地图会再次调用 `EnterMapAsync()`，该方法固定 `map_instance_id=0`，可能破坏精确恢复语义。
12. `-gamemeshAutoScenario` 只被解析，没有执行自动场景。
13. `run_two_clients_e2e.ps1` 等待 90 秒后强杀两个进程并返回 0，只证明进程启动，不证明登录、AOI、邮件或重连成功。
14. CI 主要做 Asset Check，没有强制 GameMesh EditMode、PlayMode、协议漂移和 Integration Build。

## 3. 全局执行规则

开始前必须：

1. 阅读仓库 README、`Assets/GameMesh/`、`Tools/GameMesh/`、asmdef、测试、Packages 和 ProjectSettings。
2. 执行：

   ```bash
   git status --short
   git rev-parse HEAD
   git log -5 --oneline
   ```

3. 检查同级或用户指定路径中的服务器仓库，记录服务器 HEAD 和 `proto/game.proto` hash。
4. 如果代码晚于审计基线，先验证缺口是否已修复，禁止重复实现。
5. 保留用户改动，不执行破坏性 Git 操作。
6. 直接修改代码、生成物、脚本、测试和文档，不要只给建议。
7. 不提交、不 push，除非用户在 Cursor 会话中明确授权。
8. 不手工编辑 `Generated/Game.cs`，只能由固定版本 protoc 生成。
9. 不在 Unity 主线程阻塞 `.Wait()`、`.Result` 或同步 socket；不从后台线程操作 GameObject/Transform/UI。
10. 不把 password、credential、完整 token/session 写入日志、PlayerPrefs、命令结果或截图。
11. 所有自动化脚本缺环境、缺二进制、超时、Unity 崩溃、断言缺失时必须非零退出。
12. 每阶段更新 `docs/GameMesh_Client_Foundation_Status.md`，记录真实运行结果。

---

# C0：立即消除协议漂移

## C0.1 导入最新服务器契约

从服务器仓库执行现有导出脚本，或直接使用服务器导出目录：

```bash
cd <server_repo>
./scripts/export_unity_protocol.sh <export_dir>
```

然后在 Unity 仓库导入并生成：

```text
Assets/GameMesh/Protocol/Schema/game.proto
Assets/GameMesh/Protocol/Generated/Game.cs
Assets/GameMesh/Protocol/game.desc
Assets/GameMesh/Protocol/protocol_manifest.json
```

要求：

- 客户端 schema SHA-256 必须与服务器导出 manifest 完全一致。
- manifest 记录服务器 git SHA、protocol version、schema hash、descriptor hash、protoc version 和 Google.Protobuf version。
- `required_types_missing` 必须为空。
- 生成物 namespace 必须是 `GameMesh.Protocol`。
- 固定 protoc 和 Google.Protobuf 版本，`Tools/GameMesh/versions.json` 是单一版本配置，脚本不能各写一份。
- `import_server_contract.ps1` 遇到缺少必需类型必须失败，不能只 Warning 后退出 0。
- 提供 Linux/macOS 可运行的导入/生成方式；不能让 `.sh` 只依赖 `powershell.exe`。可使用本机 `protoc` 或下载固定平台包，但要校验 SHA-256。
- 不要把缓存 zip、临时生成目录提交到 Git。

## C0.2 修正协议测试

删除“新类型应该缺失”的过时断言，改为：

- 所有 RequiredTypes 必须存在。
- client schema hash == manifest schema hash。
- server export schema hash == client schema hash。
- `Game.cs` 由当前 `game.proto` 生成。
- oneof 中存在 EnterMap、Move、AoiDelta、PlayerMailSend、MailboxChanged。
- 帧格式和最大帧长一致。

增加一键命令：

```text
Tools/GameMesh/check_protocol_contract.ps1
Tools/GameMesh/check_protocol_contract.sh
```

任何漂移必须非零退出。

## C0.3 修正文档

更新 `docs/GameMesh_Unity_Integration.md`：

- 删除“服务器尚无 MoveReq/AoiDelta/PlayerMailSendReq”等过时描述。
- 写入实际服务器 commit 和 schema hash。
- 明确协议更新顺序：服务器修改 → 导出 → Unity 导入 → 生成 → 测试 → 两端联调。

## C0 验收

至少运行：

```bash
Tools/GameMesh/check_protocol_contract.sh <server_repo_or_export>
Tools/GameMesh/run_editmode_tests.sh
```

Windows 可运行对应 `.ps1`。缺 Unity Editor 时必须报告 `NOT RUN`，不能标通过。

C0 未通过时停止，不进入 C1。

---

# C1：把现有 UI、移动、AOI 和邮件接到真实协议

## C1.1 玩家资料

登录成功后：

- 使用 `LoginRsp.profile` 更新 `Session.Attributes`。
- 如果服务器兼容路径没有 profile，则调用 `GetSelfProfileReq`，失败时明确显示错误，不能悄悄使用本地魔法默认值冒充服务器值。
- 应用 player_name、HP/MP、攻击、法强、防御、魔抗、暴击、移速、攻速和 stats_version。
- 只有 `FromServer=true` 才修改 FPS Player 的权威属性显示。
- `player_id` 以服务器返回/连接会话为准。

## C1.2 真实 EnterMap 和地图一致性

EnterMap 请求必须填写：

```text
realm_id
map_template_id
map_instance_id（首次公共图为 0；重连时使用服务器恢复语义）
map_data_version
map_data_sha256
operation_id
```

要求：

- 从导出的 `maps/1001.grid.json` 或配置资产读取真实 version/hash。
- 校验 `EnterMapRsp.map_data_version` 和 `map_data_sha256` 与本地完全相同。
- 不再使用 `FORCE_MISMATCH` 字符串模拟正式校验；负测试应构造真实错误 hash 请求。
- 应用 `spawn_position/spawn_yaw` 到本地角色。
- 应用 `self` 和 `aoi_snapshot`，原子替换 `AoiWorld`。
- 地图 hash 不一致时阻止进入 InWorld，并显示稳定 error_code。
- 同一 EnterMap operation_id 重试不能生成重复占位。

## C1.3 真实移动上报和服务器校正

新增明确的异步入口，例如：

```csharp
Task SendMoveAsync(Vector3 position, float yaw, CancellationToken ct)
```

`GameMeshWorldBinder` 在 `MoveSampler.ShouldSend()` 成功后必须：

- 构造 `MoveReq`，填写 player_id、map_instance_id、position、yaw、client_time_ms。
- 真正调用 `RequestAsync`。
- 只有成功入队后才 `MarkSent`。
- 保证同一玩家移动请求有界，不能无限创建未等待 Task。
- 处理 `MoveRsp`：正常误差平滑，超过 snap 阈值立即校正，旧 state_seq 忽略。
- `ERR_MOVE_TOO_FAST/ERR_UNWALKABLE/ERR_OUT_OF_BOUNDS` 触发权威位置校正和可读提示。
- 不在后台线程直接修改 Transform。

## C1.4 真实 AOI Push

在 `ApplyInnerPush()` 中处理 `AoiDelta`：

- 将每个 `AoiEvent` 映射为本地 Enter/Move/Leave。
- 使用 `player_id` 作为当前基础实体 ID，除非协议后续提供独立 entity_id。
- 校验 map_instance_id，不接受其他地图的 Push。
- 忽略自己作为 remote entity。
- 使用 state_seq 去重和拒绝倒退。
- Enter 幂等创建，Move 插值，Leave 幂等销毁。
- 场景卸载、Logout、换图、断线需要清理或冻结远端实体。
- 可靠 Enter/Leave 成功应用后再 ACK；解析或应用失败不能先 ACK。

修正 DTO：优先直接使用生成的协议类型或集中 mapper，不能维护第二份会漂移的协议模型。

## C1.5 真实玩家邮件

实现 `MailClient.SendAsync()`：

- 构造 `PlayerMailSendReq`。
- sender_player_id 仍填写当前玩家，但服务器会用连接身份覆盖。
- operation_id 在一次用户操作重试期间保持不变；明确成功/最终失败后清除。
- 处理 idempotent_hit、限流、收件人不存在、不能发给自己等错误码。
- 处理 `MailboxChangedNotify`，采用 debounce 后刷新 summary/list。
- Push 不可用时保留低频轮询兜底；邮箱面板关闭时不能仍每 10 秒永久轮询。
- CancellationToken 要真实传到请求层，不能只作为未使用参数。

## C1.6 统一错误显示

- 优先读取服务器顶层/业务 `error_code`，不能根据英文 message 分支。
- 建立 `GameErrorCatalog`，映射为中文 UI 文案和是否可重试。
- 日志保留 code、seq、request type、trace_id，不输出敏感内容。
- UI 防止 `_busy` 导致按钮无反馈；显示当前阶段和超时。

## C1 验收

EditMode/PlayMode 至少覆盖：

1. LoginRsp.profile 完整映射。
2. 正确/错误地图 hash。
3. EnterMap snapshot 原子应用。
4. MoveReq 真实发出且字段正确。
5. MoveRsp 小误差平滑、大误差 snap、旧 seq 忽略。
6. AoiDelta Enter/Move/Leave 的真实 protobuf 映射。
7. PlayerMailSend operation_id 重试幂等。
8. MailboxChanged debounce 刷新。
9. Logout/换图销毁远端对象。

C1 未通过时停止，不进入 C2。

---

# C2：握手、心跳、重连和世界状态恢复

> C2 需要服务器提示词中的 S1/S2 协议已经完成。开始前重新导入服务器协议，禁止客户端自行发明字段。

## C2.1 Hello 和能力协商

TCP Connect 后、Register/Login/Reconnect 前发送 `ClientHelloReq`：

- protocol version、schema SHA-256、client version、platform、build channel、capabilities。
- 校验 `ServerHelloRsp`。
- schema/version 不兼容时停止登录并显示升级提示。
- 保存 heartbeat interval、idle timeout、server time offset 和 capability 集合。
- 不能依赖反射 `HasType` 决定同一构建内的功能；构建时类型必须齐全，运行时能力由 ServerHello 协商。

## C2.2 心跳和连接生命周期

- 使用 unscaled time + 随机抖动定时发送 Heartbeat。
- 连续超时或超过服务器 idle timeout 才触发断线恢复。
- 前后台切换、Application pause/focus、场景切换和退出时行为明确。
- 单例心跳任务；Reconnect 时取消旧任务，禁止多重循环。
- `reconnectMaxTotalMs` 必须真正生效。
- 重连 single-flight，避免 Update 每帧启动多个 Task。
- 可取消 Request/Connect/Heartbeat；销毁对象后迟到 callback 不更新 UI。

## C2.3 精确重连

重连流程必须是：

```text
断线
→ 新 TCP Connect
→ ClientHello
→ Reconnect(session_id, ticket, last_server_seq)
→ 应用新 token/generation
→ 应用服务器返回或推送的 WorldSnapshot
→ 恢复 InWorld
```

要求：

- 不再无条件 `EnterMap(map_instance_id=0)`。
- 服务器明确要求重新进图时，使用返回的 map template/instance 和 operation id。
- 重连期间冻结本地移动上报。
- 成功应用完整 snapshot 前不能显示为 InWorld。
- 旧连接 callback、旧 generation Push 和旧 map instance Push 必须丢弃。
- 失败达到总时限后返回登录界面，不无限重试。

## C2.4 Push gap 和 WorldSnapshot

当前 `PushReliability` 发现 Gap 后不能只打日志：

- 标记 Resyncing，暂停对顺序敏感的增量。
- 请求 `WorldSnapshotReq` 或服务器定义的等价接口。
- 原子应用 profile、self、map route、AOI、背包和 baseline_server_seq。
- 丢弃 `<= baseline` 的旧 Push，缓存或重新处理 `> baseline` 的 Push。
- snapshot 失败时按 retryable 和退避处理；不能先 ACK 未应用消息。
- 缓存有界，溢出时断开并重新建立权威状态。

## C2.5 本地身份保存

为 Demo 提供可用但安全的本地身份：

- 可保存 device_id、最近 player_id、显示名、服务器环境。
- 不保存明文 password。
- 默认不持久化 access/fence token；若实现“记住登录”，必须使用平台安全存储并支持清除。
- 日志和 E2E 结果对 token/session 打码。
- 提供“清除本地账号信息”按钮。

## C2.6 最小死亡/复活客户端状态

如果服务器已实现：

- 展示 ALIVE/DEAD/RESPAWNING。
- DEAD 时禁止移动上报和本地射击输入。
- 显示复活按钮并发送 RespawnReq。
- 应用服务器返回位置和 HP/MP。

服务器未实现时明确标记 `BLOCKED BY SERVER`，不要做纯本地假复活。

## C2 验收

至少覆盖：

1. Hello 版本/hash 不匹配时不能 Login。
2. 心跳 RTT、时间偏移和 idle timeout。
3. 网络断开只启动一个 Reconnect 流程。
4. gw0 断线后通过 gw1 恢复原玩家、原地图和 AOI。
5. 旧 Push/generation 被拒绝。
6. 人工制造 server_seq gap 后完整快照恢复。
7. 应用退出时无悬挂 socket/task，且不阻塞 Unity 主线程。

C2 未通过时停止，不进入 C3。

---

# C3：真实双 Unity E2E、CI 和联调体验

## C3.1 实现 AutoScenario

当前 `-gamemeshAutoScenario` 只解析不执行。实现无 UI 依赖的自动状态机：

```text
hello
→ register 或读取已准备账号
→ login
→ profile
→ enter map
→ 等待 peer AOI enter
→ 移动到指定点
→ 等待 peer AOI move
→ A 给 B 发邮件
→ B 收到 MailboxChanged 并读取邮件
→ 可选断网/重连
→ logout
→ 写结果并退出
```

每个关键步骤写结构化 JSON Lines 和最终结果文件：

```json
{"event":"login_ok","player_id":1001}
{"event":"enter_map_ok","map_instance_id":9}
{"event":"aoi_peer_seen","peer_id":1002}
{"event":"mail_received","mail_id":88}
{"result":"PASS","scenario":"two-client","duration_ms":12345}
```

禁止输出 password/token/session 原文。

## C3.2 重写双客户端脚本

当前脚本等待后强杀并退出 0，必须重写。脚本需要：

- 启动两个独立 `-dataPath` 客户端。
- 使用唯一 device/name，建立协调目录交换 player_id。
- 等待明确结果文件或进程退出。
- 断言双方同一 map_instance_id。
- 断言 A 看到 B、B 看到 A。
- 断言真实 MoveReq 造成另一端 AOI Move。
- 断言邮件到达正确玩家且内容匹配。
- 可选断言一端断线重连后重新看到对方。
- 任一进程崩溃、超时、缺标记、结果不一致时非零退出。
- 成功时两个客户端自行 Logout 并以 0 退出；脚本不以强杀作为成功。
- `finally` 清理残留进程，但不能按模糊进程名误杀用户其他 Unity。
- 保存双方 Player.log、结果 JSON、服务器 commit、客户端 commit 和协议 hash。

PowerShell 和 Bash 可以分别支持 Windows/Linux build；不支持的平台明确退出 2，不能打印 PASS。

## C3.3 Unity 测试

补齐：

- EditMode：协议、FrameCodec、状态机、Push 顺序、mapper、error catalog、map hash、mail operation id。
- PlayMode：FakeGateway 半包/粘包/掉线、真实主线程 dispatch、远端对象生命周期、重连 single-flight。
- Integration：两个真实 Unity build 对真实 Gateway。
- 测试结果 XML 缺失或包含失败时脚本必须非零。

现有 `BadProtobuf_FailClosed` 必须真正让 FakeGateway 发送非法 protobuf；不能只主动 Disconnect 后断言 Disconnected。

## C3.4 CI 和构建

更新 CI：

1. 协议漂移检查。
2. Unity compile/import。
3. GameMesh EditMode。
4. GameMesh PlayMode。
5. Windows 或 Linux Integration Build。
6. 有服务器环境时运行双 Unity E2E；无环境时显示 NOT RUN，不能冒充成功。

要求：

- Unity 版本从 `ProjectVersion.txt` 读取并与 runner 匹配。
- Google.Protobuf DLL 与 asmdef 引用验证。
- 上传 test XML、Player.log、结果 JSON 和构建日志。
- 不使用 `allowDirtyBuild` 掩盖协议生成后的未提交漂移。

## C3.5 联调 UI 最小完善

- 清楚显示 Connection/Hello/Auth/World/Resync 状态。
- 显示 server build、protocol version、schema hash 简写、RTT、最后 server_seq。
- 注册后自动保留 player_id，用户不必复制粘贴。
- 显示当前 map template/instance、AOI 玩家数。
- 邮件收件人支持 player ID；若服务器提供 PlayerBrief 查询，显示校验后的名字。
- 错误显示 code + 中文文案 + 是否可重试。
- Debug 面板可关闭，正式构建不显示敏感调试信息。

## C3 验收

实际运行：

```text
Tools/GameMesh/check_protocol_contract.*
Tools/GameMesh/run_editmode_tests.*
Tools/GameMesh/run_playmode_tests.*
Tools/GameMesh/build_integration_client.*
Tools/GameMesh/run_two_clients_e2e.*
```

最终必须同时提供：

- 客户端 commit。
- 服务器 commit。
- schema hash。
- 两个 Unity 进程的结果 JSON。
- 真实退出码。
- 未运行项和原因。

---

# 4. 下一版本再做的内容

以下不属于当前基础联调阻塞项：

- 完整战斗、技能表现、背包 UI、任务、交易、公会。
- 大世界 Chunk 流式加载和跨 Cell 无缝迁移。
- Addressables 大规模资源热更新。
- 完整角色选择/多角色系统。
- 私聊、好友、公会聊天。

先让当前注册、登录、玩家资料、公共地图、移动 AOI、邮件和重连全部由真实协议驱动并可自动验收。

# 5. Cursor 每阶段最终输出格式

每阶段结束输出：

1. 客户端 HEAD、服务器协议 HEAD、schema hash。
2. 修改文件及用途。
3. 生成代码使用的 protoc/Google.Protobuf 版本。
4. 实际运行的 Unity/脚本命令、退出码和报告路径。
5. 未运行项及原因。
6. 屏幕可见的联调结果。
7. 本阶段是否通过。
8. 仍依赖服务器完成的项目。

禁止：

- 手改 `Game.cs`。
- 用 DTO 单元测试代替真实 protobuf 映射。
- 用两个进程启动成功代替双客户端 E2E。
- 吞掉 Unity 测试失败或缺少结果 XML。
- 在日志、结果文件或 UI 中输出密码和完整 Token。
- 未经授权提交或推送。
