# Luna Unity 客户端基础版收口 Cursor 提示词

> 目标：Unity 客户端可以登录、主动下线；两个真实 Unity 客户端进入同一公共地图后能互相看到；任一客户端移动时另一端能看到位置同步。
>
> 客户端仓库：`https://github.com/madongxin/luna`
>
> 本次审计客户端 HEAD：`2e43ed53267887614a358116d42b0de9c19b6822`
>
> 本次审计服务器 HEAD：`17912f2033344ee579fa388ba8f7467e1790f772`

## 你的角色

你正在修改 Luna Unity 客户端仓库。请先完整阅读本文件、`docs/GameMesh_Client_Foundation_Status.md`、`docs/GameMesh_Unity_Integration.md`，并读取同一 multi-root workspace 中服务器的 `AGENTS.md` 和 `proto/game.proto`。不要根据旧状态文档或类名判断完成，必须验证真实 TCP 与真实双 Unity 进程。

两个仓库保持独立 Git 历史。未经用户明确授权，不要 commit、push、合并或创建 PR。

## 不可违反的规则

1. 先执行 `git status --short --branch`，保留用户未提交修改。
2. 服务器 `webserver/proto/game.proto` 是公网协议唯一事实源。禁止客户端自行改字段号、复制旧 schema 或手写生成的 `Game.cs`。
3. 协议必须通过服务器导出/客户端导入脚本更新，并重新生成 descriptor 与 C#。
4. 禁止通过关闭 Hello/schema 校验来“修复”登录。
5. 密码不得写 PlayerPrefs、日志、结果 JSON 或 Git；只保留到完成注册/登录请求所需的最短时间。
6. `logout_ok` 必须来自真实 `LogoutRsp.ok` 和客户端状态断言，不能无条件记录。
7. 自动化测试必须启动两个同时运行的真实客户端进程；串行启动不能算双客户端 E2E。
8. 本轮 P0 只包含登录、主动下线、互相可见、移动同步。邮件、聊天、好友属于扩展测试，不得阻塞目标门禁。
9. `NOT RUN`、缺 Unity License、缺构建产物、缺 live Gateway 都是 `BLOCKED`，不能写成 PASS。

## 已审计事实

### 当前已经存在，不要重做

- `GameConnection` 已实现 TCP 长度帧、request seq、pending request、Push 分流和 fail-closed。
- `GameMeshClient` 已有 Hello、Heartbeat、Register、Login、EnterMap、Reconnect、WorldSnapshot、真实 `MoveReq`。
- `ApplyInnerPush` 已将 `AoiDelta` 映射到 `AoiWorld`。
- `GameMeshWorldBinder` 会为远端玩家创建 Capsule，并插值到新坐标。
- `GameMeshAutoScenario` 已有 AOI peer seen/moved 等等待逻辑。
- `run_two_clients_e2e.ps1/.sh` 已尝试检查两个客户端相同地图、双方互见和移动事件。
- 客户端与服务器地图 JSON SHA-256 一致：`ceef56586c5281dca4ce45340f511d0d577fd724b14131ae5a21d01ea7f41317`。

### 当前阻塞和缺陷

1. 当前客户端协议 SHA-256：

   ```text
   4c29a73aa7fbed19f122e122bc1832852e593f6bfaca0b7433249391e2ec643d
   ```

   当前服务器协议 SHA-256：

   ```text
   f16462b65fa998a1c1d63be4710b2be927c9ec1b8ef47756803b12798d6e8665
   ```

   客户端 manifest 仍绑定服务器 `60542e5`。当前正式服务器会在 Hello 阶段拒绝该客户端，因此目标尚未完成。

2. 客户端缺少服务器最新追加类型/字段：
   - `MapManifestEntry`
   - `ServerHelloRsp` 的配置/地图清单字段
   - `SessionReplacedNotify`
   - `GameResponse.session_replaced = 76`

3. `RegisterAsync()` 完成后清空 `LaunchArgs.Password`；自动场景随后调用 `LoginAsync()`，会发送空凭据。服务器账号使用真实密码哈希，这会阻断自动登录。

4. `LogoutAsync()` 忽略 LogoutRsp 的业务成功与否，并在 finally 中清理本地状态；`GameMeshAutoScenario` 随后无条件记录 `logout_ok`。

5. 自动场景 Logout 后才写 `result.json`，但 Logout 会清零 `map_instance_id`。外层 E2E 脚本又要求结果中的 map instance 非零，因此即使游戏链路正确也会失败。

6. Linux 脚本通过 `A_PID="$(start_client ...)"` 获取 PID。命令替换中的后台 Unity 进程会占住 subshell/stdout，导致 A 结束后才可能启动 B，无法形成同时在线的双客户端。

7. 所有 `Tools/GameMesh/*.sh` 在 Git 中是 `100644`，而 GitHub Actions 的 protocol job 直接执行 checker、没有先 `chmod` 或用 `bash`，Linux runner 会报 `Permission denied`。

8. 当前最小场景把邮件也列为必过断言。邮件故障会掩盖登录/AOI/移动问题，不符合本轮目标隔离原则。

9. `SessionReplacedNotify` 同步后需要在 `OnPush/ApplyInnerPush` 中显式处理；未知字段静默丢弃不等于实现。

10. 自动场景的两个进程可能共享 PlayerPrefs。命令行设备 ID 虽不同，但测试不应读取另一个进程留下的 player ID/host/account 状态。

## 完成定义

| 能力 | 必须证据 |
| --- | --- |
| 协议 | schema 与服务器字节一致，hash `f16462b6…`，manifest 指向兼容服务器提交，生成物可重复生成且工作树干净 |
| 登录 | 两个独立账号都经过 Hello → Register/Login → EnterMap，UI/结果显示真实 player_id、session、同一非零 map instance |
| 互相可见 | 两端 `AoiWorld` 都有对方，场景中存在对应 `RemotePlayer_*`，不能只检查 DTO |
| 移动同步 | A→B 和 B→A 至少各一次，远端位置变化超过阈值且 state_seq 单调递增 |
| 主动下线 | A 收到成功 LogoutRsp 后断开；B 收到 AOI Leave 并删除远端 GameObject；随后 B 也成功 Logout |
| 自动化 | EditMode、PlayMode、Integration Build、两个并发 Unity Player 的目标场景均真实 PASS |

## 阶段 C0：同步服务器 `17912f2` 公网协议

先完成本阶段；失败时停止，不进入业务修改。

1. 从同一 workspace 的服务器仓库执行正式导出，或使用服务器 `scripts/export_unity_protocol.sh` 的完整产物。
2. 使用客户端 `Tools/GameMesh/import_server_contract.ps1/.sh` 导入，不要手工复制部分类型。
3. 使用固定版本 `protoc 25.3` 重新生成：
   - `Assets/GameMesh/Protocol/Schema/game.proto`
   - `Assets/GameMesh/Protocol/Generated/Game.cs`
   - `Assets/GameMesh/Protocol/game.desc`
   - `Assets/GameMesh/Protocol/protocol_manifest.json`
4. manifest 必须记录：
   - `source_repo=https://github.com/madongxin/webserver`
   - 实际兼容的 server commit
   - schema hash `f16462b65fa998a1c1d63be4710b2be927c9ec1b8ef47756803b12798d6e8665`
   - descriptor hash
   - protoc/Google.Protobuf 版本
   - required types
5. 将 `MapManifestEntry`、`SessionReplacedNotify` 和新的 GameResponse oneof case 加入客户端契约检查。
6. Hello 成功后读取服务器返回的地图清单，按 `map_template_id` 找到 1001，校验 data version/hash；不存在或不匹配时禁止进图并展示稳定错误码。不要只依赖代码中的硬编码 fallback hash。
7. `ApplyInnerPush` 显式处理 `SessionReplacedNotify`：停止重连、清理会话/远端实体、展示顶号原因。保留本地账号身份。
8. 修复 Linux 脚本执行方式：提交正确 executable bit，或在所有 CI 调用处明确使用 `bash`。协议 job 不能再 `Permission denied`。
9. 更新 `docs/GameMesh_Client_Foundation_Status.md` 中旧 HEAD、旧 hash 和“未提交收口”表述。

验证：

```bash
bash Tools/GameMesh/check_protocol_contract.sh /absolute/path/to/webserver
git diff --check
git status --short --branch
```

C0 停止条件：schema、descriptor、manifest、生成代码任一不一致即停止。

## 阶段 C1：修复注册→登录和主动 Logout

### 注册与登录

1. 为自动场景提供明确的 `RegisterThenLoginAsync` 流程，或让 Register/Login 接受局部 credential 参数。
2. 注册成功后，登录请求必须仍使用同一份非空密码；无论成功/失败，登录请求结束后在 finally 中清空内存中的命令行密码。
3. 不将密码写入 PlayerPrefs、日志、event/result JSON。
4. Login 成功必须检查顶层 `rsp.ok`、`LoginRsp.ok`、非零 player_id、非空 session/token/generation，并等待 EnterMap 完成后才记录 `login_ok`。
5. 自动 E2E 模式不读取旧 PlayerPrefs 的 player ID/host/account；每个进程使用本轮独立身份。

### 主动 Logout

1. 将 `LogoutAsync` 改成返回结构化结果，例如 `LogoutResult` 或 `Task<bool>`，至少包含：
   - request sent
   - top-level ok
   - `LogoutRsp.ok`
   - error code/message
   - transport disconnected
2. 只有收到服务器权威成功时才记录 `logout_ok`。
3. Logout 成功后：停止 Heartbeat/重连、清空 token/session/map/AOI/远端 GameObject、断开 TCP。
4. 普通 Logout 应保留本地 player_id/device/display name，方便再次登录；只有“清除本地账号信息”才删除本地身份。
5. Logout 失败时仍应停止自动重连并安全断开，但必须记录 `logout_failed`，不能伪装成功。
6. 防止 Logout 与 Heartbeat/Move/Reconnect 并发；进入 LoggingOut 状态后拒绝新的业务请求。

必须增加 EditMode/PlayMode 测试：

- 注册成功后登录仍发送非空、相同 credential；完成后内存密码清空。
- Login 顶层成功但 body 失败不得进入 Authenticated/InWorld。
- Logout body 失败不得记录成功。
- Logout 成功后不自动重连，AOI 和远端 GameObject 被清理，本地账号身份按设计保留。
- `SessionReplacedNotify` 走显式顶号路径。

## 阶段 C2：重写“目标专用”真实双客户端 E2E

保留邮件场景作为扩展测试，新增一个只验证本轮目标的场景，例如 `presence-move-logout`。

### Unity 场景步骤

1. 同时启动 A、B 两个真实 Player，各用唯一 device/name 和相同规则生成的非空测试密码。
2. A、B 分别 Hello、注册、登录、进图。
3. 记录登出前的：player_id、map_instance_id、spawn position、state_seq。
4. 断言两个 map instance 非零且相等。
5. A 的 `AoiWorld` 包含 B，B 的 `AoiWorld` 包含 A；同时场景中找到相应 `RemotePlayer_<entityId>`。
6. A 移动到与出生点明显不同且合法的位置；B 看到 A 坐标变化和 state_seq 增长。
7. B 再移动；A 做同样断言。
8. A 发送 Logout，必须等待权威成功；B 必须收到 Leave，`AoiWorld` 和远端 GameObject 都删除 A。
9. B 发送 Logout 并等待权威成功。
10. 两个进程写出结果后自行以 0 退出。

### 结果文件

`result.json` 使用登出前保存的稳定值，不读取已清空 Session：

```json
{
  "result": "PASS",
  "role": "a",
  "player_id_before_logout": 123,
  "map_instance_id_before_logout": 456,
  "hello_ok": true,
  "login_ok": true,
  "peer_seen": true,
  "peer_move_seen": true,
  "logout_rsp_ok": true,
  "peer_leave_seen": true,
  "schema_sha256": "...",
  "client_commit": "...",
  "server_commit": "..."
}
```

事件 JSONL 必须包含可解析字段，而不是只靠子字符串：peer_id、map_instance_id、old/new position、old/new state_seq、Logout error code。

### Bash/PowerShell 启动脚本

1. Bash 禁止使用 `A_PID="$(start_client ...)"`。正确做法是直接后台启动、重定向各自 stdout/stderr，然后立即取 `$!`：

   ```bash
   "$CLIENT" ... >"$A_DIR/stdout.log" 2>&1 &
   A_PID=$!
   "$CLIENT" ... >"$B_DIR/stdout.log" 2>&1 &
   B_PID=$!
   ```

2. 超时时主动终止仍存活进程，并返回非零。
3. 使用 `wait` 收集两个真实退出码；不能只检查文件存在。
4. 结构化解析两个 result/events 文件；禁止 `"aoi_peer_seen" in text` 这种可能误命中的检查。
5. PowerShell 版本保持同等断言。
6. 为两个进程隔离 PlayerPrefs/用户数据；自动场景不依赖共享 PlayerPrefs。
7. 邮件断言移到 `extended-mail` 场景，不阻塞 `presence-move-logout`。

## 阶段 C3：测试与 CI 门禁

1. FakeGatewayServer 必须同步当前协议，并支持测试 Hello 地图清单、SessionReplaced、Login/Logout 成功和失败分支、AOI Enter/Move/Leave。
2. EditMode 覆盖协议映射、凭据生命周期、Logout 状态机、AOI state_seq/旧包丢弃。
3. PlayMode 覆盖远端 GameObject 创建、位置插值、Leave 删除和 Logout 清场。
4. Integration Build 必须来自当前干净 HEAD，并把 commit/schema 写入结果。
5. GitHub Actions 中：
   - protocol checker 可执行；
   - Unity build artifact 上传；
   - E2E job 下载该 artifact；
   - 提供 live Gateway 或明确由集成环境启动；
   - main/tag 缺必要基础设施时显示 BLOCKED，不得产生假 PASS。
6. 真实 E2E 的日志中不得出现密码、token、完整 reconnect ticket。

## 必须执行的验证

根据操作系统选择 `.ps1` 或 `.sh`：

```bash
git status --short --branch
git rev-parse HEAD
bash -n Tools/GameMesh/*.sh
bash Tools/GameMesh/check_protocol_contract.sh /absolute/path/to/webserver
Tools/GameMesh/run_editmode_tests.sh
Tools/GameMesh/run_playmode_tests.sh
Tools/GameMesh/build_integration_client.sh
GAMEMESH_E2E_GATEWAY=1 \
GAMEMESH_HOST=127.0.0.1 \
GAMEMESH_PORT=8081 \
Tools/GameMesh/run_two_clients_e2e.sh /absolute/path/to/GameMeshClient
git diff --check
git status --short --branch
```

如脚本尚未设置 executable bit，可在修复前临时用 `bash script.sh` 验证，但最终 Git/CI 必须永久正确。

## 最终输出格式

1. `Client baseline`：开始/结束 commit、worktree、Unity 版本、server/client/schema/map hash。
2. `Changed files`：逐文件说明。
3. `Target verdicts`：Login、Logout、mutual visibility、A→B move、B→A move、AOI Leave 分别列 PASS/FAIL/BLOCKED。
4. `Tests run`：命令、退出码、测试数量、报告/日志路径。
5. `Two-player evidence`：两个登出前 player_id、同一 map instance、位置和 state_seq 变化、LogoutRsp、Leave。
6. `Not run`：Unity License、构建产物、live Gateway 等缺失项。
7. 不要自动提交，不要用旧报告或 FakeGateway 测试代替真实双 Unity E2E。

