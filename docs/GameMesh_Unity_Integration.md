# GameMesh Unity 联调说明

客户端只连接 Gateway TCP（默认 `127.0.0.1:8081`），不连接 brpc / GameLogic / Redis / MySQL。

## 依赖（已钉死）

| 项 | 版本 |
|----|------|
| Unity | 2022.3.62f3c1 |
| protoc | 25.3 |
| Google.Protobuf | 3.25.3（`Assets/GameMesh/Plugins/Google.Protobuf.dll`） |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 |
| 协议来源 | [madongxin/webserver](https://github.com/madongxin/webserver) `proto/game.proto` @ `37b19773` |

恢复 DLL：

```powershell
Invoke-WebRequest https://www.nuget.org/api/v2/package/Google.Protobuf/3.25.3 -OutFile Tools/GameMesh/cache/Google.Protobuf.3.25.3.nupkg
# 解压后复制 lib/netstandard2.0/Google.Protobuf.dll 到 Assets/GameMesh/Plugins/
```

## 导入协议并生成 C#

```powershell
.\Tools\GameMesh\import_server_contract.ps1 -Source <server-repo-or-export-dir>
.\Tools\GameMesh\generate_csharp_proto.ps1
```

不要手改 `Assets/GameMesh/Protocol/Generated/Game.cs`。

当前服务器契约 **缺少** Unity 文档要求的：`PlayerAttributes`、`Vec3`、`EntitySnapshot`、`MoveReq`、`AoiDelta`、`PlayerMailSendReq`、`MailboxChangedNotify`。客户端不会自造字段号；这些能力以 DTO/能力探测实现，等服务器导出后再导入即可接线。

## 运行 Demo

1. 打开项目，Play `IntroMenu` 或任意已进 Build Settings 的场景。
2. `GameMeshClient` 会自动创建（`RuntimeInitializeOnLoadMethod`），左上角 IMGUI 面板可注册/登录。
3. 可选菜单：`GameMesh/Demo/Install Into Built Scenes`。
4. 登录成功后加载 `MainScene` 并发送 `EnterMap(realm=1, template=1001, instance=0)`。
5. `Tab` 解锁鼠标以便点 UI。联调模式禁用 Sprint（`SprintSpeedModifier=1`）。

命令行：

```
-gamemeshHost 127.0.0.1
-gamemeshPort 8081
-gamemeshDevice device-a
-gamemeshPassword <not stored>
-gamemeshName Alice
-gamemeshAutoScenario login-enter
-gamemeshPeerPlayerId 10002
```

## 地图导出

```
菜单：GameMesh/Map/Export Current Scene
Batch：-executeMethod GameMesh.Editor.MapExportBatch.ExportMainScene
可选：-gamemeshCopyTo <server-maps-dir>
```

输出（给服务器加载，不是 .unity / Prefab / NavMesh 二进制）：

- `maps/1001.grid.json`
- `maps/1001.grid.json.sha256`（当前 MainScene：`ceef56586c5281dca4ce45340f511d0d577fd724b14131ae5a21d01ea7f41317`）

格式与服务器 S2 约定一致：`bounds_*` 为 `[x,y,z]`，`walkable_rle` 为 `[value, count, ...]`，出生点为 `{id, position, yaw}`。水平面 X/Z，Y 为高度。`aoi_cell_size=12`，`nav_sample_step=1`。进图 hash 必须与该 `.sha256` 一致，否则 `ERR_MAP_DATA_MISMATCH`。

## 测试

```powershell
$env:UNITY_PATH="C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe"
.\Tools\GameMesh\run_editmode_tests.ps1
.\Tools\GameMesh\run_playmode_tests.ps1
```

双客户端真实 E2E：先 `build_integration_client.ps1`，设置 `GAMEMESH_E2E_GATEWAY=1` 后运行 `run_two_clients_e2e.ps1`。未提供 Gateway 时脚本返回 2（未执行，不是通过）。
