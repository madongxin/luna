# 服务器侧如何使用 Luna 仓库

两台机器：

| 机器 | 角色 |
|---|---|
| 阿里云 Linux | 跑 `webserver` 门禁与 Formal Gateway |
| 开发机 Windows | 开发并 `git push` Luna；跑双 Unity Player |

客户端：`https://github.com/madongxin/luna`。用 git 拉，不要用 GitHub tarball（大且易超时）。

## 阿里云拉取完整 Luna git 仓库（主路径）

```bash
git clone https://github.com/madongxin/luna.git ~/luna
export LUNA_REPO="$HOME/luna"
cd "$LUNA_REPO"
git fetch origin
git checkout main
git pull --ff-only origin main
git rev-parse HEAD
```

`LUNA_REPO` 必须是完整 git 工作树，含：

- `Assets/GameMesh/Protocol/Schema/game.proto`
- `Assets/GameMesh/Protocol/protocol_manifest.json`
- `Tools/GameMesh/check_protocol_contract.sh`

只拷 proto 不够。`GAMEMESH_REQUIRE_LUNA_CONTRACT=1` 时未设置 `LUNA_REPO` 必须失败，不得写成 PASS。

若 `github.com` clone/fetch 失败：记录完整报错并 `BLOCKED`，不要假装有仓库。备用：开发机 `Builds/luna-pack/luna-*.bundle`（gitignore，需另行拷贝）。

## 协议钉死值

| 项 | 值 |
|---|---|
| schema SHA-256 | `f16462b65fa998a1c1d63be4710b2be927c9ec1b8ef47756803b12798d6e8665` |
| 兼容服务器 commit | `17912f2033344ee579fa388ba8f7467e1790f772` |
| 地图 1001 hash | `ceef56586c5281dca4ce45340f511d0d577fd724b14131ae5a21d01ea7f41317` |
| protocol_version | `1` |

Formal Gateway 的 `ServerHelloRsp.maps` 必须包含 `map_template_id=1001`，且 version/hash 与上表一致。

## 在阿里云 webserver 根目录跑门禁

```bash
export LUNA_REPO="$HOME/luna"
./scripts/check_luna_protocol_contract.sh
# 期望：luna_protocol_contract=PASS

./scripts/build.sh Debug
./scripts/client_ready_gate.sh
# 期望：CLIENT READY PASS

./scripts/stable_gate.sh --full
# 期望：STABLE CANDIDATE PASS
```

TCP 使用云上 `build/test/game_tcp_e2e_client`，不要执行 Windows `GameMeshClient.exe`。  
`--full` 含 sanitizers、20 轮 E2E、默认 30min 负载、2h soak。

## 真实 Unity 双进程 E2E（仅 Windows 开发机）

Player 在 gitignore 的 `Builds/`，**不会出现在 git clone 里**。Linux 也不要跑 `.exe`。

开发机：`C:\Users\dongx\FirstFPS\Builds\GameMeshClient\GameMeshClient.exe`  
云上把 Formal Gateway 游戏 TCP 对公网开放后，开发机：

```powershell
$env:GAMEMESH_E2E_GATEWAY = "1"
.\Tools\GameMesh\run_two_clients_e2e.ps1 -HostName <ALIYUN_GATEWAY_HOST> -Port 8081
```

场景：`presence-move-logout`。缺 Gateway 或缺 exe 时退出码 `2` = NOT RUN。
