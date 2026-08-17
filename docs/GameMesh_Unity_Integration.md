# GameMesh Unity 联调说明

客户端只连接 Gateway TCP（默认 `127.0.0.1:8081`），不连接 brpc / GameLogic / Redis / MySQL。

## 依赖（已钉死）

| 项 | 版本 |
|----|------|
| Unity | 2022.3.62f3c1 |
| protoc | 25.3 |
| Google.Protobuf | 3.25.3（`Assets/GameMesh/Plugins/Google.Protobuf.dll`） |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 |
| 协议来源 | [madongxin/webserver](https://github.com/madongxin/webserver) `proto/game.proto` @ `17912f2033344ee579fa388ba8f7467e1790f772` |
| schema SHA-256 | `f16462b65fa998a1c1d63be4710b2be927c9ec1b8ef47756803b12798d6e8665` |
| protocol_version | `1` |

版本只写在 `Tools/GameMesh/versions.json`。不要手改 `Assets/GameMesh/Protocol/Generated/Game.cs`。

## 协议更新顺序

1. 服务器修改 `proto/game.proto` 并提交。
2. 从服务器仓库导出或直接指向仓库根目录。
3. Unity 导入：`Tools/GameMesh/import_server_contract.ps1 -Source <server>`（Linux/macOS 用 `.sh`）。
4. 脚本用固定 protoc 25.3 生成 C#；缺少必需类型时必须非零退出。
5. 运行 `Tools/GameMesh/check_protocol_contract.*` 与 EditMode 测试。
6. 两端用同一 schema hash 联调。

当前已导入 Hello、Heartbeat、WorldSnapshot、Respawn、MapManifest、SessionReplaced 以及既有 Register/Login/Move/AOI/Mail 类型。连接后必须先 `ClientHello`，再注册或登录。Hello 返回的 `maps` 必须包含 template `1001`，且 hash/version 与本地 `maps/1001.grid.json.sha256` 一致，否则禁止进图。

## 运行 Demo

1. 打开项目，Play `IntroMenu` 或任意已进 Build Settings 的场景。
2. `GameMeshClient` 会自动创建，左侧 IMGUI 面板可注册/登录/发邮件。
3. 登录成功后加载 `MainScene`，发送带真实 `map_data_version/map_data_sha256` 的 `EnterMap`。
4. `Tab` 或 `F1` 解锁鼠标。联调模式禁用 Sprint。
5. 注册后的 `player_id` 会保留在内存和本地身份（不含密码/Token）。

命令行：

```
-gamemeshHost 127.0.0.1
-gamemeshPort 8081
-gamemeshDevice device-a
-gamemeshPassword <not stored>
-gamemeshName Alice
-gamemeshAutoScenario presence-move-logout
-gamemeshRole a
-gamemeshResultDir <dir>
-gamemeshCoordDir <dir>
-gamemeshPeerPlayerId 10002
```

## 地图导出

```
菜单：GameMesh/Map/Export Current Scene
Batch：-executeMethod GameMesh.Editor.MapExportBatch.ExportMainScene
```

- `maps/1001.grid.json`
- `maps/1001.grid.json.sha256`（当前 MainScene：`ceef56586c5281dca4ce45340f511d0d577fd724b14131ae5a21d01ea7f41317`）

进图时客户端发送该 hash；与 `EnterMapRsp` 不一致则阻止进入 InWorld。

## 测试

```powershell
$env:UNITY_PATH="C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe"
.\Tools\GameMesh\check_protocol_contract.ps1 Tools\GameMesh\cache\server-export
.\Tools\GameMesh\run_editmode_tests.ps1
.\Tools\GameMesh\run_playmode_tests.ps1
```

双客户端真实 E2E：先 `build_integration_client.ps1`，设置 `GAMEMESH_E2E_GATEWAY=1` 后运行 `run_two_clients_e2e.ps1`（默认场景 `presence-move-logout`）。未提供 Gateway 时脚本返回 2（NOT RUN，不是通过）。邮件场景使用 `-gamemeshAutoScenario extended-mail`。
