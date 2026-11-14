# 观察端 AOI 外推（E3 设计，未实现）

状态：**仅设计**。等确认后再改代码。不改协议字段、不新增消息、不改心跳与移动上行。LEAVE（op=3）同一帧删模型保持不变。

开关草案：`aoi_extrapolate`（`GameMeshClientConfig`，**默认 false**）。关闭 = 现有插值/追点行为（见 `aoi_client_interp.md`）。

---

## 0. 服务端约定（以服务端为准；实现前再对照）

开 `GAMEMESH_AOI_EXTRAPOLATE=1` 时，MOVE / ENTER 带：

| 字段 | 含义 |
|---|---|
| `position` | 发出时的呈现坐标 |
| `vx`, `vz` | 水平速度 |
| `move_start_ms` | 非 0 表示速度外推有效 |
| `server_time_ms` | 该事件的服务端时间 |

发送节奏（服务端）：首次、变向、到点停止、近处约每 4s 关键帧、远处约每 10s 关键帧。

关外推时：`move_start_ms == 0` → 客户端保持现有行为（不读速度）。

外推式：`position + (vx, vz) × (估计服务器时间 − server_time_ms)`（时间单位秒）。

---

## 1. 现状（已对照代码）

| 组件 | 行为 |
|---|---|
| `RemotePoseBuffer` | 注释写明不读 `vx/vz/move_start_ms`；按 delay 取样；可沿末段 `CoastMaxSec` 滑行 |
| `AoiInterpFollow` | 不读速度场；追延迟采样点 |
| `ProtocolMapper.ToEntityDto` | **丢弃** `Vx/Vz/MoveStartMs`（DTO 无字段） |
| `AoiSampleClock.MaxGapSec = 3.2` | 回拨时间放置时把行程 gap 封顶 3.2s；事件稀疏到 4–10s 时会把真实间隔压扁 → **需在实现时改或旁路** |

压测观察计数（E1 已加）：`aoi_move_start_ms_nonzero_ratio` 可在开服务端开关后核对事件是否带速度。

---

## 2. 速度模式状态机

仅当事件 `MoveStartMs != 0` **且** `aoi_extrapolate==true` 时进入速度模式。

```
Idle/Interp ──(MOVE|ENTER, MoveStartMs≠0)──► Extrapolating
Extrapolating ──(MOVE|ENTER, MoveStartMs≠0, 新速度/点)──► Extrapolating（slew 衔接）
Extrapolating ──(到点停止事件：速度≈0 或显式 stop)──► Arriving → Idle
Extrapolating ──(MoveStartMs==0 或开关关)──► 回退现有插值
任意 ──(LEAVE)──► 同一帧销毁，清状态
```

每个远端实体存（建议挂在 `RemoteEntityState` 或 view 私有态）：

| 字段 | 说明 |
|---|---|
| `mode` | Interp / Extrapolating / Arriving |
| `anchorX/Z` | 事件 position |
| `vx, vz` | 事件速度 |
| `moveStartMs` | 事件 MoveStartMs |
| `serverTimeMs` | 事件 server_time_ms |
| `renderX/Z` | 当前呈现 |
| `slewFromX/Z`, `slewUntilLocalMs` | 新事件衔接 |
| `frozen` | 超过外推上限 |
| `stats` | snap/freeze/slew 计数（metrics 开关） |

---

## 3. 服务器时间估计

目标：得到 `estServerNowMs`，使 `t = (estServerNowMs - event.server_time_ms) / 1000`。

算法草案：

1. 每次可靠 AOI 事件（带 `server_time_ms≠0`）：`skew = server_time_ms - localMonoMs`。
2. 保留最近 **N=8** 个 skew 样本。
3. 取 **绝对值最小** 的若干（默认 3）做中位数，得 `skewHat`。
4. 平滑：`skewSmooth += α * (skewHat - skewSmooth)`，`α` 默认 **0.2**。
5. 单次跳变上限：`|ΔskewSmooth| ≤ 50ms` / 事件（防尖刺）。
6. `estServerNowMs = localMonoMs + skewSmooth`。
7. 无样本时：不进速度模式（写「无数据」/保持插值）。

参数（配置项）：`aoiExtrapSkewSamples=8`, `aoiExtrapSkewPick=3`, `aoiExtrapSkewAlpha=0.2`, `aoiExtrapSkewStepMaxMs=50`。

---

## 4. 新事件衔接

1. 算出外推本应在的点 `P_pred`，事件锚点 `P_evt`。
2. 偏差 `d = |P_pred - P_evt|`：
   - `d ≤ snapThreshold`（默认 **3m**）：启动 **slew**，在 **150–300ms**（配置，默认 200ms）内把 `render` 从当前位置插到「外推轨道上的对应点」。
   - `d > snapThreshold`：吸附到 `P_evt`，`snapCount++`。
3. **到点停止**：进入 `Arriving`，继续沿当前速度（或指向 `P_evt`）走到事件 position，**不得**停在外推超调点；到达 ε（0.2m）后清速度 → Idle。

---

## 5. 外推上限与可行走

| 项 | 默认 | 说明 |
|---|---|---|
| 近距上限 | **10s** | 配置 `aoiExtrapNearMaxSec`，待压测校准 |
| 远距上限 | **25s** | 配置 `aoiExtrapFarMaxSec` |
| 超限 | 冻结在上限位置，`freezeCount++`，等新事件 | |
| 可行走 | **第一版不做**地图查询；若后加，仅拒绝穿墙外推，不改权威点 | |

近/远判定可复用现有 far-gap（如 `aoiInterpFarGapMs`）或与观察者平面距离阈值（另配，默认不写死 AOI 半径）。

---

## 6. ENTER / LEAVE / 重连

| 事件 | 行为 |
|---|---|
| ENTER + MoveStartMs≠0 | 建/复用实体，进入 Extrapolating；短时 reenter 可复用 view（现有 reenter 窗） |
| ENTER + MoveStartMs==0 | 现有插值路径 |
| MOVE | 按状态机更新锚点/速度 |
| LEAVE | **同一帧** `ReleaseView`/`DestroyImmediate` 路径；清 buffer 与外推态；不进 slew |
| LEAVE 后很快 ENTER | 新一代 `PoseEpoch`；不继承旧速度 |
| 重连 / 全量快照 | 清空外推态；按快照实体重建；MoveStartMs==0 则插值 |

---

## 7. 与现有插值的关系

```
aoi_extrapolate=false → 完全走现有 aoiInterp* / 追点逻辑
aoi_extrapolate=true 且 MoveStartMs≠0 → 速度外推优先
aoi_extrapolate=true 且 MoveStartMs==0 → 回退现有插值（含 coast）
```

不与 `aoiInterpCoast` 叠加速度双外推：速度模式下关闭 coast。

`AoiSampleClock.MaxGapSec`：速度模式下 **不走** PlaceRewound 的 3.2s 封顶；或将 MaxGapSec 提高到 ≥ 远距关键帧（实现时二选一，默认旁路）。

---

## 8. EditMode 测试方案（实现阶段）

1. MoveStartMs=0 → 行为与关开关一致。
2. 恒速 vx 外推 2s 位置误差 < 0.05m。
3. 新事件偏差 1m → slew 内收敛，无 snap 计数。
4. 偏差 5m → snap++，位置=事件点。
5. 到点停止：render 停在事件点而非外推点。
6. 外推 11s（近上限 10s）→ frozen。
7. LEAVE 后无实体、无残留 view。
8. skew 尖刺被 step max 限制。

## 9. 联合验证

1. 服务端开 `GAMEMESH_AOI_EXTRAPOLATE=1`，客户端开 `aoi_extrapolate`。
2. 导出 E1 的 `aoi_move_start_ms_nonzero_ratio` 应明显 >0。
3. `/aoi/visible` 与 `aoi-visible-*.json` 对账。
4. 关服务端或关客户端开关 → 视觉回退，无异常瞬移风暴。

## 10. 风险与回滚

| 风险 | 缓解 |
|---|---|
| 时钟估计漂 | 样本+步进上限；无样本不外推 |
| 稀疏关键帧 + 旧 MaxGapSec | 速度模式旁路 3.2s |
| 超调穿模 | 上限冻结；到点 Arriving |
| 协议缺字段 | MoveStartMs==0 回退 |

回滚：关 `aoi_extrapolate`（默认已关）；不改协议故服务端可独立关外推。

---

## 11. 本任务不做

- 不改任何运行时代码（E3）。
- 不把 Vx/Vz/MoveStartMs 写入 DTO（等确认实现时再加，且仅观察端使用）。
