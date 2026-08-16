# Luna Unity 客户端基础版收口：Cursor 分阶段执行提示词

> 目标仓库：`https://github.com/madongxin/luna`  
> 目标分支：`main`  
> 本次审计基线：`38a0042a62a1e3975a5315a7e742dbc5342102f4`  
> 服务器仓库：`https://github.com/madongxin/webserver`  
> 服务器审计基线：`60542e51ed5f7e757fced13cb2a069c29739aa36`  
> Unity 当前 schema SHA-256：`aed5c952a1aa817a13464af8ae05c14d14c19da0ceedd6b61663d2b39f255bcb`  
> 服务器当前 schema SHA-256：`4c29a73aa7fbed19f122e122bc1832852e593f6bfaca0b7433249391e2ec643d`

请把本文件整体交给 Unity 客户端仓库中的 Cursor。先让服务器完成其 S0/S1 并导出最终契约，再按 C0 → C1 → C2 → C3 顺序执行。前一阶段未通过时停止。

## 1. 本轮目标

把当前 Unity Demo 收口为能与最新 GameMesh 服务器真实联调的基础客户端：

- 连接后先完成协议握手，再允许注册、登录和重连。
- 心跳、RTT、服务器时间差、断线检测和有界重连可用。
- 真实注册、登录、权威属性、进图、移动、AOI、50 人公共图语义、邮件和 Logout 可用。
- Push 序号缺口和跨 Gateway 重连后使用完整世界快照恢复。
- 能显示被顶号、协议/地图/配置不兼容、过载等稳定错误。
- 两个真实 Unity 进程对最新服务器执行自动化 E2E 并返回可靠退出码。

## 2. 审计结论：保留已经完成的实现

当前客户端已经完成或基本完成：

- 4 字节大端 ProtoFraming、pending request、Push 分流、请求超时和有界队列。
- 注册、登录、Logout、Reconnect 基础调用与 Session 状态机。
- `LoginRsp.profile/GetSelfProfile` 到玩家权威属性映射。
- 真实 EnterMap version/hash、spawn、self 和 AOI snapshot 应用。
- 真实 `MoveReq/MoveRsp`、移动限频与服务器位置校正。
- 真实 `AoiDelta` Enter/Move/Leave 和远端实体模型。
- 真实玩家邮件发送、邮箱查询、MailboxChanged 触发刷新。
- Push ACK、gap 检测、`Resyncing` 状态。
- 安全的本地身份存储，不保存 password/token。
- 自动场景与双客户端结果文件断言框架。
- 协议自检脚本、EditMode/PlayMode 测试和 CI 框架。

不要重新设计网络层，不要手写第二套 DTO 协议，不要重做已接通的 Move/AOI/Mail。

## 3. 当前阻塞项

1. 当前 Unity manifest 的 `source_commit` 是服务器 `145a647...`，不是最新 `60542e5`。
2. 客户端与服务器 schema hash 不一致；`check_protocol_contract.sh ../webserver` 已会正确失败。
3. 客户端没有最新的 `ClientHello/Heartbeat/WorldSnapshot/Respawn` 生成类型。
4. 服务器 Formal 模式默认要求 Hello，因此当前 Unity 无法开始注册或登录。
5. Push gap 和 Reconnect `NeedFullSnapshot` 当前只进入 `Resyncing` 并打印“BLOCKED BY SERVER”，不会真正请求和应用世界快照。
6. 当前 `FullStateSnapshotRsp` 旧版只应用 baseline，没有恢复 profile、map route、self、AOI、life_state。
7. `GameErrorCatalog` 只读取部分子响应错误，没有优先处理服务器新增的顶层 `error_code/retryable/server_time_ms/trace_id`。
8. 没有明确的重复登录顶号通知处理；普通网络断开与 session replaced 无法区分。
9. CI 缺 Unity License 时会把 Unity tests 跳过并成功；E2E 缺 live Gateway 时也会被视为非阻塞，不能作为发布证据。
10. 历史文档仍写旧 HEAD/dirty/未 push，真实双 Unity E2E 尚未在最新服务器运行。
11. 当前 `Tools/GameMesh/check_protocol_contract.sh` 未保留可执行位，直接运行会退出 126；用 `bash` 调用可通过客户端自检，但发布脚本应统一修正文件权限。

## 4. 全局执行规则

开始前必须：

```bash
git status --short
git rev-parse HEAD
git log -5 --oneline
```

同时读取同级或用户指定的服务器仓库：

```bash
git -C <server_repo> status --short
git -C <server_repo> rev-parse HEAD
sha256sum <server_repo>/proto/game.proto
```

约束：

- 若代码晚于审计基线，先确认缺口，禁止重复实现。
- 保留用户已有改动，不执行破坏性 Git 操作。
- 直接修改代码、生成物、测试、脚本和文档，不要只给建议。
- 不提交、不 push，除非用户在本次 Cursor 会话中明确授权。
- `Assets/GameMesh/Protocol/Generated/Game.cs` 只能由固定 protoc 生成，不得手工编辑。
- 服务器 `proto/game.proto` 是公网协议唯一事实源；Unity 不维护分叉 schema。
- 不在 Unity 主线程执行 `.Wait()`、`.Result` 或同步 Socket；后台线程不得操作 GameObject、Transform、UI 或 Unity API。
- 不记录 password、credential、完整 token/session。
- 所有网络流程必须有 timeout、CancellationToken、single-flight 和可恢复状态。
- 必需测试缺 Unity、缺 License、缺客户端 Build、缺 Gateway 或超时，发布门禁必须非零并写 `BLOCKED/NOT RUN`，不能算 PASS。

---

# C0：导入服务器最终协议，先恢复两端兼容

## C0.1 导入而不是复制粘贴

服务器完成 S0/S1 后，从服务器导出：

```bash
cd <server_repo>
./scripts/export_unity_protocol.sh <export_dir>
```

在 Unity 仓库执行：

```bash
Tools/GameMesh/import_server_contract.sh <export_dir>
Tools/GameMesh/generate_csharp_proto.sh
Tools/GameMesh/check_protocol_contract.sh <server_repo>
```

更新并提交到工作区：

```text
Assets/GameMesh/Protocol/Schema/game.proto
Assets/GameMesh/Protocol/Generated/Game.cs
Assets/GameMesh/Protocol/game.desc
Assets/GameMesh/Protocol/protocol_manifest.json
```

要求：

- manifest 的 `source_commit` 必须等于实际服务器 HEAD。
- schema hash、descriptor hash、protocol version、frame format、max frame bytes 完全一致。
- 固定 protoc 与 Google.Protobuf 版本；生成脚本校验下载包 SHA-256。
- import 发现缺少 required type、旧 server commit、hash 不一致或生成物漂移时非零退出。
- Linux/macOS 脚本不能只转调 PowerShell；Windows 脚本与 `.sh` 语义一致。
- 所有对外 `.sh` 脚本必须提交可执行位，并且不依赖调用者当前目录。
- 不导入服务器内部 `session.proto/gamelogic_rpc.proto/gamedb.proto`。

`required_types` 增加：

```text
ClientHelloReq ServerHelloRsp HeartbeatReq HeartbeatRsp
FullStateSnapshotRsp WorldSnapshotReq RespawnReq RespawnRsp
SessionReplacedNotify（如果服务器 S1 增加）
```

并保留现有 Register/Login/Move/AOI/Mail 类型检查。

## C0.2 协议回归测试

新增或更新 EditMode 测试：

- 新类型和 oneof 实际存在。
- client schema == manifest == server schema。
- 顶层错误字段存在。
- append-only descriptor 与客户端生成类型一致。
- 任一 hash 被篡改时脚本必须失败。

## C0 验收

```bash
bash Tools/GameMesh/check_protocol_contract.sh <server_repo>
bash Tools/GameMesh/run_editmode_tests.sh
```

C0 未通过时不得继续写业务适配。

---

# C1：握手、心跳、时间同步和统一错误

## C1.1 连接后强制 Hello

连接状态调整为：

```text
Disconnected → Connecting → Handshaking → Connected
→ Authenticating → Authenticated → EnteringWorld → InWorld
```

TCP 建立后立即发送 `ClientHelloReq`：

```text
protocol_version
schema_sha256
client_version
platform
build_channel
capabilities
```

要求：

- 收到成功 `ServerHelloRsp` 前禁止 Register/Login/Reconnect/普通业务请求。
- Hello 只能 single-flight，使用独立 timeout 和连接 generation。
- 校验服务器 protocol、schema、最小客户端版本和必要 capability。
- 不匹配时 fail-closed，显示稳定中文错误，不自动绕过。
- 删除运行时代码中的 `ServerBlockedNotes`、`HelloBlocked` 等旧占位逻辑。
- 不使用服务器 legacy no-hello 开关作为正式兼容方案。

## C1.2 Heartbeat、RTT 与时间差

按 `ServerHelloRsp.heartbeat_interval_ms` 启动心跳：

- 使用单调时钟计算 RTT，不能用可回拨墙钟测延迟。
- 通过请求发送时间、响应时间和 server time 估算 `server_time_offset_ms`。
- 保存平滑 RTT、jitter、last heartbeat received。
- 心跳不能重叠；连接 generation 改变后旧 timer/callback 自动失效。
- 连续超时或超过 idle timeout 时进入 Reconnecting，不制造多个重连任务。
- 断开、Logout、OnDestroy 时取消 timer，不泄漏 Task。

## C1.3 顶层公开错误

统一错误解析顺序：

1. `GameResponse.error_code/retryable/server_time_ms/trace_id`。
2. 对应子响应的 error_code（兼容旧服务器）。
3. 本地传输错误。

更新 `GameErrorCatalog`，至少覆盖：

```text
协议/客户端版本不兼容
未认证/会话过期/旧 fence
地图版本不匹配/地图满/路由过期
AOI 需要全量同步
邮件限流/收件人不存在
请求过载/限流/依赖不可用
重复登录被顶号
```

UI 以 error_code 为准，不依赖服务器英文 message；显示简短 trace_id 仅用于联调，日志仍需脱敏。

## C1.4 最小配置版本

读取服务器 Hello 或配置清单中的：

```text
gameplay_config_version
map_manifest_version
map data_version/sha256
```

版本不兼容时阻止进图，提示更新资源。复用现有 `maps/1001.grid.json` 及 sha256，不建立第二份 magic version。

## C1 验收

EditMode/PlayMode 至少覆盖：

- Hello 成功、hash 错误、版本过低、超时。
- Hello 前 Login 被客户端状态机阻止。
- Heartbeat RTT/time offset、超时重连、timer 取消。
- 顶层错误优先级和 retryable 映射。
- 配置/地图版本不匹配阻止 InWorld。

---

# C2：完整快照、重连、顶号和复活

## C2.1 Push gap 自动请求 WorldSnapshot

当前 Push gap 不能只进入 `Resyncing`。实现：

```text
发现 expected_seq 与 received_seq 缺口
→ 暂停应用后续增量且不 ACK 未应用消息
→ single-flight WorldSnapshotReq(last_applied_server_seq)
→ 校验完整快照
→ 原子替换 Session + self + AoiWorld
→ Push baseline 重置
→ 恢复 InWorld
```

应用 `FullStateSnapshotRsp` 的全部基础字段：

```text
profile
realm_id/map_template_id/map_instance_id
gamelogic_instance_id（仅诊断）
owner_epoch/route_version（仅保存诊断，不由客户端选路）
self/aoi_entities
baseline_server_seq/snapshot_version
recovery_reason/life_state
```

要求：

- 快照 `ok=false`、空 self、地图不一致、版本倒退时不能清空旧世界后假装成功。
- 先构建临时模型并完整校验，最后在 Unity 主线程一次性切换。
- 去掉旧实现中只 `Aoi.Clear()` 和重置 baseline、却不应用实体的行为。
- 快照过程中到达的 Push 要么有界暂存并按 seq 应用，要么丢弃后再次拉快照；策略必须有测试。

## C2.2 Reconnect 恢复

Reconnect 成功后：

- 应用服务器返回的新 session/fence/generation。
- `NeedFullSnapshot=true` 时立即走同一个 WorldSnapshot 流程。
- 不调用 `EnterMap(map_instance_id=0)` 破坏服务器恢复路由。
- 恢复 map/self/AOI 后才进入 InWorld。
- 恢复失败要回到可重新登录状态，不能卡在 Resyncing。
- 旧连接 generation 的 callback 不得修改新 Session/UI。

## C2.3 重复登录/顶号

处理服务器的 `SessionReplacedNotify`（或服务器最终定义的等价公开通知）：

- 展示“账号已在其他设备登录”的稳定原因。
- 禁止自动重连旧 session。
- 清理 token/session/AOI/远端实体和 pending requests。
- 返回登录界面；不要清除非敏感的 device/player_id 便捷信息。
- 普通网络断开仍按自动重连策略处理，二者必须区分。

## C2.4 死亡与复活

- 应用 `PlayerAttributes.life_state` 与快照 life_state。
- DEAD 时禁用本地移动/技能提交，但保留镜头和 UI。
- 复活按钮发送带稳定 operation_id 的 `RespawnReq`。
- 重复点击复用同一 in-flight；响应成功后应用服务器 self/life_state。
- 失败显示 error_code；不得客户端自行改 HP 或坐标冒充复活。

## C2 验收

新增 EditMode/PlayMode 测试：

1. Push gap 请求且只请求一次快照。
2. 完整快照原子恢复 profile、self、AOI 和 baseline。
3. 非法/过期快照不破坏当前世界。
4. Reconnect + NeedFullSnapshot 恢复原地图，不 EnterMap(0)。
5. 被顶号后不自动重连，普通掉线仍重连。
6. Respawn operation_id、失败和成功状态。

---

# C3：真实双 Unity E2E 和发布门禁

## C3.1 完善自动场景

扩展 `GameMeshAutoScenario` 和结构化 `events.jsonl/result.json`，至少记录并断言：

```text
hello_ok heartbeat_ok
register_ok login_ok profile_ok
enter_map_ok aoi_peer_seen aoi_peer_moved
mail_sent mail_received
snapshot_recovered reconnect_ok
session_replaced（独立场景）
logout_ok
```

结果文件必须包含：

```text
client_commit
server_commit
schema_sha256
map_manifest_version
gateway
duration_ms
result/error_code
```

不得包含密码、token、session、完整 trace。

## C3.2 真实场景，而不是 FakeGateway 替代

保留 FakeGateway 单元/PlayMode 测试，但真实门禁必须使用最新服务器：

1. 构建 Linux 或 Windows Integration Client。
2. 启动两个真实 Unity 进程连接同一 VIP/Gateway 入口。
3. 两玩家注册、登录、加载权威属性。
4. 进入同一公共地图并互相看到。
5. A 移动，B 收到 AOI Move。
6. A 给 B 发邮件，B 收到 Push 并打开邮件。
7. 断开 A 的 gw0 链路，通过 gw1 重连并恢复世界快照。
8. 人为制造 Push gap，客户端恢复且不残留幽灵实体。
9. 新进程重复登录 A，旧进程收到顶号通知并退出。
10. 两个客户端正常 Logout。

`run_two_clients_e2e.sh/.ps1` 必须：

- 等待并验证每个事件，不只检查进程启动。
- 进程崩溃、超时、结果缺失、Gateway 缺失均非零退出。
- 发布模式下退出码 2 也是失败。
- 清理时只终止自己记录的 PID，不按进程名误杀。
- 保存服务端与两客户端日志路径。

另增加重复登录与重连场景脚本，不要把所有竞态塞入一个不可诊断脚本。

## C3.3 CI 与发布事实

调整 CI：

- 协议自检始终必跑。
- Release 分支/标签必须有 Unity License 并真实跑 EditMode、PlayMode 和 Integration Build；缺 License 为 BLOCKED，不得绿色发布。
- 真实 E2E 放到有服务器依赖和 Unity Build 的 runner；没有 live Gateway 不能标 PASS。
- 上传 XML、Player.log、events.jsonl、result.json 和两端 manifest。

更新 `docs/GameMesh_Client_Foundation_Status.md`：

- 写实际客户端/服务器 HEAD、schema hash、工作区状态。
- 只记录本次真实执行的测试。
- 分开 `PASS/FAIL/BLOCKED/NOT RUN`。
- 删除“服务器不提供 Hello/Heartbeat/WorldSnapshot/Respawn”的过时描述。

## C3 验收命令

按实际平台运行对应 `.sh` 或 `.ps1`：

```bash
Tools/GameMesh/check_protocol_contract.sh <server_repo>
Tools/GameMesh/run_editmode_tests.sh
Tools/GameMesh/run_playmode_tests.sh
Tools/GameMesh/build_integration_client.sh
GAMEMESH_E2E_GATEWAY=1 Tools/GameMesh/run_two_clients_e2e.sh <client_binary>
```

## 5. 基础版完成标准

只有以下全部满足，才能写 `UNITY FOUNDATION PASS`：

- 两端 schema/hash/commit 完全一致。
- Formal 模式下 Hello、Heartbeat 正常，未使用 legacy no-hello。
- Register/Login/Logout/Profile/Map/Move/AOI/Mail 全部真实联调通过。
- Push gap、跨 GW Reconnect、完整快照和顶号处理通过。
- EditMode、PlayMode、Integration Build、真实双 Unity E2E 均有当前 commit 证据。
- 没有必需步骤 SKIP/NOT RUN。

不阻塞本轮基础版：好友列表、私聊、公会、交易、复杂技能战斗、完整热更新和无缝 Cell 大世界。

## 6. Cursor 最终输出

每阶段完成后输出：

1. 当前 Unity/服务器 HEAD、工作区状态和 schema hash。
2. 修改文件清单。
3. 连接、Hello、Login、Reconnect、Snapshot 状态流。
4. 执行命令、退出码和报告路径。
5. PASS/FAIL/BLOCKED/NOT RUN 清单。
6. 真实双 Unity E2E 的结果目录。
7. 尚未完成但不阻塞基础版的范围。

禁止手工改生成代码、关闭 Formal Hello、放宽协议 hash、用 FakeGateway 冒充真实联调或通过跳过测试宣称完成。
