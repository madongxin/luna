# 服务器侧如何使用 Luna 仓库

给 `webserver` 门禁用。客户端仓库：`https://github.com/madongxin/luna`。  
当前约定 HEAD：以 `git rev-parse HEAD` 为准（本说明随 `main` 更新）。

## 不要从 GitHub 拉源码

GitHub clone / tarball 可能超时。任选其一：

1. **本机已有仓库（优先）**

   ```bash
   export LUNA_REPO="/mnt/c/Users/dongx/FirstFPS"
   # Windows 原生路径：C:\Users\dongx\FirstFPS
   ```

2. **用 git bundle（不访问 github.com）**

   ```bash
   git clone /mnt/c/Users/dongx/FirstFPS/Builds/luna-pack/luna-f830c09.bundle luna
   cd luna
   export LUNA_REPO="$PWD"
   ```

   若 bundle 文件名随 commit 变化，到 `Builds/luna-pack/` 下取最新 `luna-*.bundle`。  
   重新打包：在 Luna 仓库执行 `Tools/GameMesh/export_client_ready_pack.ps1`。

3. **协议文件副本（只够对 hash，不够当完整 git 仓库）**

   `Builds/luna-pack/protocol/{game.proto,protocol_manifest.json,1001.grid.json.sha256}`  
   `client_ready_gate.sh` / `check_luna_protocol_contract.sh` 需要完整 Luna 树（含 `Tools/GameMesh/check_protocol_contract.sh`），不要只拷 proto。

## 协议钉死值

| 项 | 值 |
|---|---|
| schema SHA-256 | `f16462b65fa998a1c1d63be4710b2be927c9ec1b8ef47756803b12798d6e8665` |
| 兼容服务器 commit | `17912f2033344ee579fa388ba8f7467e1790f772` |
| 地图 1001 hash | `ceef56586c5281dca4ce45340f511d0d577fd724b14131ae5a21d01ea7f41317` |
| protocol_version | `1` |

`GAMEMESH_REQUIRE_LUNA_CONTRACT=1` 时必须设置 `LUNA_REPO`，否则协议门禁失败，不得写成 PASS。

Hello 的 `ServerHelloRsp.maps` 必须包含 `map_template_id=1001`，且 version/hash 与上表一致，否则 Unity 客户端禁止 `EnterMap`。

## 在 webserver 仓库执行的门禁

工作目录必须是 **webserver 根目录**，不是 Luna。

```bash
export LUNA_REPO="/mnt/c/Users/dongx/FirstFPS"
./scripts/check_luna_protocol_contract.sh
# 期望：luna_protocol_contract=PASS

./scripts/build.sh Debug
./scripts/client_ready_gate.sh
# 期望：CLIENT READY PASS

./scripts/stable_gate.sh --full
# 期望：STABLE CANDIDATE PASS
```

`client_ready_gate.sh` 的 TCP 步骤使用服务器自己的 `build/test/game_tcp_e2e_client`，不是 Unity Player。  
`stable_gate.sh --full` 含 sanitizers、20 轮 E2E、默认 30min 负载、2h soak；缩短时长或缺少 `shellcheck` 必须报 `STABLE BLOCKED`。

## 真实 Unity 双进程 E2E（Windows Player）

二进制（gitignore，不在 Git 里）：

```
C:\Users\dongx\FirstFPS\Builds\GameMeshClient\GameMeshClient.exe
C:\Users\dongx\FirstFPS\Builds\luna-pack\GameMeshClient\GameMeshClient.exe
```

需要 Formal Gateway（默认 `127.0.0.1:8081`）后，在 **Luna 仓库**执行：

```powershell
$env:GAMEMESH_E2E_GATEWAY = "1"
.\Tools\GameMesh\run_two_clients_e2e.ps1 -HostName 127.0.0.1 -Port 8081
```

Linux：

```bash
GAMEMESH_E2E_GATEWAY=1 GAMEMESH_HOST=127.0.0.1 GAMEMESH_PORT=8081 \
  bash Tools/GameMesh/run_two_clients_e2e.sh /path/to/GameMeshClient
```

场景名：`presence-move-logout`。缺 Gateway 或缺二进制时退出码 `2` = NOT RUN，不是 PASS。

本机当前 Player 是 **Windows x64**。Linux runner 不能直接跑该 `.exe`；Linux 门禁用 `game_tcp_e2e_client`，Windows 上再用上述 Player 做双 Unity 进程。
