# GameMesh 客户端基础能力状态

日期：2026-08-18
客户端：本提交起的 `main`；对 live Gateway `47.96.22.16:8083` 的 E2E 修复见下文
服务器协议 HEAD：`17912f2033344ee579fa388ba8f7467e1790f772`
schema SHA-256：`f16462b65fa998a1c1d63be4710b2be927c9ec1b8ef47756803b12798d6e8665`
descriptor SHA-256：`078461f2c0bfa23c3d806b51dff1734be06777a65e332fe772cf4aa223c4aefb`
protocol_version：`1`
min_supported_protocol_version：`1`
protoc：25.3
Google.Protobuf：3.25.3

## 本轮收口

- C0：从服务器 `17912f2` 导入 `game.proto`（LF 规范化后 hash 与审计基线一致）；required_types 含 `MapManifestEntry` / `SessionReplacedNotify`。Hello 后按地图清单校验 template `1001`；`ApplyInnerPush` 显式处理顶号 Push。协议 CI 使用 `bash Tools/GameMesh/check_protocol_contract.sh`。
- C1：`RegisterThenLoginAsync` 在登录请求发出前保留同一份非空密码；登录结束后清空内存密码。自动场景不读 PlayerPrefs。`LogoutAsync` 返回 `LogoutResult`，仅权威成功记 `logout_ok`，并保留本地 player_id/device/name。
- C2：新增 `presence-move-logout` 双客户端场景；`result.json` 使用登出前 player_id/map_instance。Bash 后台并发启动两个 Player。邮件断言移到 `extended-mail`。
- C3：FakeGateway Hello 带地图清单，并覆盖 Login/Logout 失败与 SessionReplaced。CI 上传 Integration Build，E2E job 下载该产物；main/tags 缺 License 或缺 live Gateway 时 BLOCKED。

## 本机实测

| 命令 | 退出码 | 报告 |
|------|--------|------|
| `check_protocol_contract.ps1 <webserver-17912f2>` | 0 | schema `f16462b6…` 与服务器 HEAD 一致 |
| EditMode | 0 | `TestResults/editmode.xml` 46/46 |
| PlayMode | 0 | `TestResults/playmode.xml` 5/5 |
| `build_integration_client.ps1` | 0 | `Builds/GameMeshClient/GameMeshClient.exe`，并写入 Player 内 `protocol_manifest.json` |
| `run_two_clients_e2e.ps1 -HostName 47.96.22.16 -Port 8083` | 1 | `Logs/e2e-20260818-091936`：Hello/Login/EnterMap/双向 AOI 可见 PASS；MoveReq 被 Gateway 以空 body `ERR_STALE_SEQ` 拒绝 |

## 未运行 / BLOCKED

| 项 | 原因 |
|----|------|
| 真实双 Unity E2E 全场景 | Gateway `47.96.22.16:8083` 已通。互相看见已过。A 的 `MoveReq`（`GameRequest.seq=6`，unix `client_time_ms`）回包 `seq=6 type=None ok=false error_code=ERR_STALE_SEQ`，无 `MoveRsp`。因此移动同步 / 登出 AOI Leave 未跑完 |
| 跨 GW 重连 / 人为 Push gap / 顶号 E2E | `run_session_replaced_e2e` 仍为驱动脚本占位，退出 2 |
| CI Unity 测试/出包 | 需要仓库 `UNITY_LICENSE` secret |
| CI 真实 E2E | 需要 `GAMEMESH_E2E_GATEWAY=1` 和可访问的 live Gateway |
