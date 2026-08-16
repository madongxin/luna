# GameMesh 客户端基础能力状态

日期：2026-08-16  
客户端 HEAD：`38a0042`（工作区有未提交的 60542e5 协议收口）  
服务器协议 HEAD：`60542e51ed5f7e757fced13cb2a069c29739aa36`  
schema SHA-256：`4c29a73aa7fbed19f122e122bc1832852e593f6bfaca0b7433249391e2ec643d`  
descriptor SHA-256：`99137cbf8274e771069a40178c27b5e36357865aa449533c152e75d97ad9c40d`  
protocol_version：`1`  
min_supported_protocol_version：`1`  
protoc：25.3  
Google.Protobuf：3.25.3  

## 本轮收口

- C0：从服务器 `60542e5` 导入 `game.proto`（LF 规范化后 hash 与审计基线一致），生成 C#；required_types 含 Hello/Heartbeat/WorldSnapshot/Respawn。
- C1：TCP 后进入 `Handshaking`，强制 `ClientHelloReq`；按 Hello 间隔发 `HeartbeatReq`；顶层 `error_code/retryable/trace_id` 优先；UI 用稳定中文错误。
- C2：Push gap / Reconnect `NeedFullSnapshot` 请求 `WorldSnapshotReq` 并原子应用完整快照；死亡禁用移动并发送 `RespawnReq`；顶号停止自动重连并保留本地 player_id/device。
- C3：AutoScenario 记录 `hello_ok`/`heartbeat_ok` 及 commit/schema/gateway；CI 在 main/tags 缺 License 或缺 live Gateway 时 BLOCKED。

`SessionReplacedNotify` 未出现在公网 `game.proto`；客户端按顶层 `ERR_SESSION_REPLACED` / `ERR_FENCE_STALE` 与重连失败处理顶号。

## 本机实测

| 命令 | 退出码 | 报告 |
|------|--------|------|
| `check_protocol_contract.ps1 <webserver-60542e5>` | 0 | schema `4c29a73a…` 与服务器 HEAD 一致 |
| EditMode | 0 | `TestResults/editmode.xml` 37/37 |
| PlayMode | 0 | `TestResults/playmode.xml` 4/4 |
| `build_integration_client.ps1` | NOT RUN | 本轮未出包 |
| `run_two_clients_e2e.ps1` | 2 | NOT RUN：无 `GAMEMESH_E2E_GATEWAY` |

## 未运行 / BLOCKED

| 项 | 原因 |
|----|------|
| 真实双 Unity E2E | 需要 live Gateway 与 Integration Client |
| 跨 GW 重连 / 人为 Push gap / 顶号 E2E | 需要 live 集群；`run_session_replaced_e2e` 无 Gateway 时退出 2 |
| CI Unity 测试/出包 | 需要仓库 `UNITY_LICENSE` secret |
