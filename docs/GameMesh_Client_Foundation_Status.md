# GameMesh 客户端基础能力状态

日期：2026-08-15  
客户端 HEAD：`084fcd4cc133974b390c4e234b236cf34cd931d2`（工作区未提交）  
服务器协议 HEAD：`145a64753aacd0d9e1dc7916edee81d15f183148`  
schema SHA-256：`aed5c952a1aa817a13464af8ae05c14d14c19da0ceedd6b61663d2b39f255bcb`  
protoc：25.3  
Google.Protobuf：3.25.3  

未提交、未 push。

## C0 协议漂移 — 通过

命令：

```
Tools/GameMesh/check_protocol_contract.ps1 Tools/GameMesh/cache/server-export
```

退出码：`0`  
服务器/客户端 schema 一致。`required_types_missing` 为空。  
`import_server_contract.ps1` 缺少必需类型时改为抛错退出。`.sh` 不再只转调 `powershell.exe`。

EditMode（本机 Unity `2022.3.62f3c1`）：

- 报告：`TestResults/editmode.xml`
- `29 passed / 0 failed`（含 `Protocol_RequiredTypesPresent`、`FoundationMappingTests`）

## C1 真实协议接线 — 通过

已接线：

- `LoginRsp.profile` / `GetSelfProfileReq` → `Session.Attributes`，仅 `FromServer=true` 改 FPS 属性
- `EnterMap` 发送 `maps/1001.grid.json.sha256`（`ceef5658...`）与 `data_version=1`，校验响应，应用 spawn/AOI snapshot
- `SendMoveAsync` 发送 `MoveReq`；`MoveRsp` 平滑/吸附；旧 `state_seq` 忽略
- `ApplyInnerPush` 映射真实 `AoiDelta` / `MailboxChangedNotify`；应用失败不 ACK
- `MailClient.SendAsync` 发送 `PlayerMailSendReq`，重试保持 `operation_id`
- `GameErrorCatalog` 按 `error_code` 显示中文

## C2 重连与恢复 — 有条件通过

已实现：

- 重连 single-flight + `reconnectMaxTotalMs`
- 重连后不再 `EnterMap(map_instance_id=0)`
- Push gap 进入 `Resyncing`，不 ACK 未应用消息
- 本地身份只存 device/player_id/name/host；清除按钮；密码/Token 不落盘
- 销毁时不在主线程 `.GetResult()`

`BLOCKED BY SERVER`（当前 `game.proto` 无这些消息，客户端未自造字段）：

- `ClientHelloReq` / `ServerHelloRsp`
- Heartbeat
- `WorldSnapshotReq`
- 死亡/复活 `RespawnReq`

## C3 自动化与 CI — 部分通过

已实现：

- `-gamemeshAutoScenario` 状态机，写 `events.jsonl` / `result.json`（无 token/password）
- `run_two_clients_e2e.ps1/.sh` 按结果文件断言；无 Gateway 时退出 `2`
- `BadProtobuf_FailClosed` 由 FakeGateway 发送非法 protobuf
- CI：`.github/workflows/gamemesh-foundation.yml`（协议检查 + 有 License 时跑测试 + E2E NOT RUN）

PlayMode：`TestResults/playmode.xml`，`3 passed / 0 failed`（含非法 protobuf fail-closed）。

未运行：

| 项 | 退出码 | 原因 |
|----|--------|------|
| `build_integration_client.ps1` | 未跑 | 本阶段未强制出包 |
| `run_two_clients_e2e.ps1` 真实双端 | `2` | 未设置 `GAMEMESH_E2E_GATEWAY=1`，本机无已启动 Gateway |

## 屏幕可见联调 UI

左侧面板显示：连接/进图/重连/同步状态、player_id、地图模板/实例、AOI 人数、协议 hash 简写、中文错误、注册后保留的 player_id、清除本地账号、邮箱收件人 ID。Hello/快照/复活在面板上标注为服务器未提供。

## 仍依赖服务器

1. Hello / 能力协商与心跳  
2. WorldSnapshot 以填补 Push gap  
3. 死亡/复活协议  
4. 真实 Gateway 上的双 Unity E2E（需本机或 CI 启动 webserver Gateway）
