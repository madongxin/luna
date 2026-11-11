# 观察者侧 AOI 插值缓冲（仅客户端）

状态：已按确认实现。默认 delay 200ms；远层 250ms + 300ms 外推；8m 内 1.5 倍移速追上、默认不瞬移；取样与外推两个开关，默认都关。对账消息只写设计，不实现。

范围：观察者看到的其他玩家。自己角色不进这条缓冲。不改协议字段、心跳、移动上行。不读 `vx`、`vz`、`move_start_ms`。不写死格子半径。

---

## 0. 对照：服务端行为 vs 现有客户端

### 0.1 服务端（按本次转述，客户端不实现、不写死）

- 视野是 32 米格子窗口。半径当前 2 格，后续可能 1 格。客户端只认 ENTER/MOVE/LEAVE，不按距离自己算进出。
- 新进窗口：ENTER，生成模型。
- 窗口内坐标变化：约每 180ms 一条 MOVE；远层后续可能 600ms–1s。
- 呈现点跨出窗口：LEAVE（op=3）。服务端后续保证送达。
- 大于 0.4 米的位移按 `move_speed` 走，不瞬移。
- 不使用 `vx` / `vz` / `move_start_ms`。

AOI 事件里没有格子半径字段，也没有每实体 `move_speed`。远程移速客户端目前用 `MoveStepLimit.ServerMetersPerSec = 10`。自己角色的 `PlayerAttributes.MoveSpeed` 不套到别人身上。

### 0.2 现有客户端（已核对代码，转述基本正确）

播放（`RemotePlayerView.Update`）：

- 权威点是最新一次已接受的 MOVE/ENTER `position`（`_lastAuthorityX/Z`）。
- 模型按上一段水平位移/时间差得到的速度 `MoveTowards` 该点，速度上限 10 m/s，单帧位移再被 `ClampRenderDelta` 限制在 0.05s。
- 走到该点后停住，直到下一条 MOVE 或 LEAVE。
- `RemotePoseBuffer` 深度为 2，线性插值实现还在，但 Update **没有**按缓冲时间取样。`GameMeshClientConfig.interpolationDelayMs = 280` 只写入 `_delay`，`PlaybackDelay()` 目前主要用于卡顿日志。

因此：MOVE 间隔拉长或后一条晚到时，模型会在最新点上停住，下一条一到再突然动。这是当前实现，不是服务端停步。

LEAVE：

- `AoiWorld.ApplyDelta(op=3)` 按 PlayerId（EntityId 为 0 时也用 PlayerId）从表里删掉，并触发 `BodyRemoved`。
- `GameMeshWorldBinder.OnBodyRemoved` 同一调用栈里 `HideNow` + `Destroy` 模型和名字板。
- `SyncRemotes` 对表中已不存在的视图再删一次。
- `PruneSilent`（约 25s）**不再**作为离视野删除。静默超时不能当 LEAVE 用。

缺口：

- `AoiWorld.Clear()` / `PruneSilent` 删表时不发 `BodyRemoved`，全量快照替换时幽灵模型要等到当帧 `SyncRemotes`。
- Unity `Destroy` 在帧末才真正拆对象。同一帧 LEAVE 后立刻 ENTER，可能碰到正在销毁的 GameObject。

自己角色：`PlaceLocal` / `HoldStable` / `SendMoveAsync`，不走 `RemotePlayerView`。本次保持不变。

---

## 1. 目标与硬约束

目标：

1. 窗口内别人走路不再「走到最新点就停、再突然动」。
2. 收到 LEAVE 的同一帧模型（含名字板）消失，不留幽灵，不被插值缓冲推迟。

硬约束：

- 只改客户端。
- 不改协议字段；不改心跳和移动上行。
- LEAVE（op=3）同一帧删模型，缓冲不得延迟这条路径。
- 自己角色不经过本次插值缓冲。
- 每项新行为有开关，**默认关**，可回滚。
- 不读 `vx`、`vz`、`move_start_ms`。
- 不写死 AOI 半径。

开关（建议全部放在 `GameMeshClientConfig`，默认关）：

| 开关 | 默认 | 打开后 |
|---|---|---|
| `aoiInterpEnabled` | false | 远程按延迟时间在已收到点序列上取样，不再追最新点 |
| `aoiInterpCoastEnabled` | false | 缓冲耗尽后沿上一段速度外推，上限见配置 |
| `aoiInterpDebugDraw` | false | 画点序列和当前渲染点 |
| `aoiInterpMetricsEnabled` | false | 累计缓冲/耗尽/外推/瞬移/对账删除 |
| `aoiReconcileEnabled` | false | 对账删除；本次只设计，实现阶段仍保持关 |

关 `aoiInterpEnabled` 时，远程播放保持现在的「走到最新 MOVE 点并停下」。LEAVE 立即删除保持现状（已实现），不做成可关的延迟删除。

---

## 2. A. 插值缓冲

### 2.1 渲染时间

每个远程实体维护：

- 点序列：`(x, y, z, yaw, t_local, state_seq)`，`t_local` 由 `AoiEvent.server_time_ms` 映射到本地 `unscaledTime`（沿用现有 `SampleTime`：确认时间回拨仍接受点，本地时间只向前）。
- 延迟 `delay`（秒）。
- 渲染时间 `t_render = Time.unscaledTime - delay`。

取样：在 `t_render` 两侧的相邻点之间 **线性** 插值（yaw 走最短角）。不在最新点之外取样，除非打开外推且仍在外推窗口内。

开关打开后，不再把 Transform 的目标设成「最新收到的点」，而是设成 `t_render` 对应的路径点。

建议默认（需拍板）：`aoiInterpDelayMs = 200`，下限 `aoiInterpDelayMinMs = 150`，上限 `aoiInterpDelayMaxMs = 250`。

### 2.2 自适应算法（不是「自适应」三个字）

延迟按 **实体** 各自算，不用全图一个值。

每个实体保留：

- `last_arrive`：上一条被接受的 MOVE/ENTER 的本地到达时间
- `ewma_gap`：到达间隔的指数平均
- `ewma_absdev`：`|gap - ewma_gap|` 的指数平均（抖动）
- `delay`

参数（配置项，建议默认）：

- `alpha = 0.25`（新间隔权重）
- `k_jitter = 1.5`
- `slew_up_ms = 40`（每条 MOVE 延迟最多上调）
- `slew_down_ms = 20`（每条 MOVE 延迟最多下调，下调慢于上调，避免刚抬起来又掉下去造成停顿）

第 1 个点：不更新间隔，`delay = delay_default`。

第 n 个点（n≥2）：

```
gap = now - last_arrive
gap = clamp(gap, 0.05, 2.0)          # 单条间隔裁剪，避免一次卡顿把 EWMA 打穿
ewma_gap = (1-alpha)*ewma_gap + alpha*gap
ewma_absdev = (1-alpha)*ewma_absdev + alpha*|gap - ewma_gap|
raw = ewma_gap * hold_ratio + k_jitter * ewma_absdev
# hold_ratio 见 2.3：近层 0，远层 0.5（需拍板）
target = clamp(raw, delay_min, delay_max_this_entity)
delta = target - delay
if delta > 0: delay += min(delta, slew_up)
else:         delay += max(delta, -slew_down)
last_arrive = now
```

近层 180ms、抖动 20ms、`hold_ratio=0` 时：

- `raw ≈ 0 + 1.5*0.02 = 0.03s`，被下限抬到 **150ms**
- 实际渲染比最新点晚 150–200ms，180ms 节奏下下一条通常在走到旧点之前已经进序列 → 不在最新点停住

一次 500ms 迟到：

- `gap=0.5`，`ewma_gap` 向 0.5 靠，`ewma_absdev` 变大，`target` 顶到该实体上限
- 本条仍可能外推/短暂停；后续几条延迟抬高，同样抖动不再每次停

**明确不用** 现有 `PlaybackDelay = clamp(gap + late + 0.05, 280ms, 3.25s)`。那条公式会把延迟顶到数秒，模型看起来冻住。

### 2.3 远层间隔 600ms–1s：按实体取延迟

建议：**按实体分别取**。远近混在一个观察者视野里，全局一个 delay 会让近的人多 0.5–1s 延迟，或者让远的人仍然在点上停。

建议两档上限（需拍板，默认先用 A）：

| 方案 | delay_max | 1s 间隔时走到最新点后还要等 |
|---|---|---|
| A. 全局 150–250ms + 300ms 外推 | 250ms | 约 1.0 - 0.25 - 0.30 = 0.45s 仍可能停 |
| B. 远层 `delay_max = clamp(0.5*ewma_gap, 250ms, 700ms)` | 最高 700ms | 1s 间隔基本还能插在两点之间，显示更晚 |
| C. A+B 都开 | 远层更高 delay，外推作保险 | 最稳，远层延迟最大 |

判定远层：不读格子半径。用 `ewma_gap > aoiInterpFarGapMs`（建议默认 400ms）把该实体切到远层上限。格子从 2 改 1 时客户端不用改。

文档建议实现 C，但 **默认配置先走 A**（delay 150–250，外推 300ms），远层停顿作为已知限制写进 G。是否抬远层 delay 由你拍板。

### 2.4 点序列长度与过期

环形缓冲，每实体：

- 容量 `aoiInterpBufferSize = 12`（180ms × 12 ≈ 2.2s；1s × 8 也够 delay+外推）
- 满了丢最旧点；正在作为插值左端的点不丢，改为丢它更左边的
- 过期：`t_local < t_render - 0.05s` 且不是当前左端时删除
- 只保留「已被接受」的点。严格更旧的 `state_seq` 不进缓冲（现有规则）
- 相同 `state_seq`、新 `position`：进缓冲，当作同一次走路的沿途采样
- `AoiWorld` 仍只存最新实体状态；点序列只活在 `RemotePlayerView` / 独立 `RemotePoseBuffer` 上，LEAVE 时整表扔掉

300 观察者 × 12 点 ≈ 很小，不按帧分配堆内存（预分配环形数组）。

ENTER 只有一个点：取样就返回该点，**不外推**。

---

## 3. B. 缓冲耗尽

### 3.1 外推一小段

`t_render` 超过最新点时间，且 `aoiInterpCoastEnabled`：

- 方向 = 最新两点的水平位移（模长 < 0.05m 则视为静止，不外推）
- 速度 = `distance / dt`，上限 `move_speed`（10 m/s，见 3.3）
- 外推时长 `coast = t_render - t_tip`，上限 `aoiInterpCoastMaxMs = 300`
- `coast` 超过上限：停在外推终点，动画收回站立
- 不读 vx/vz/move_start_ms

关外推开关：与现在一样，停在最新点。

### 3.2 下一条 MOVE 回到序列，避免瞬移/回头

新点进序列后，下一帧仍用 `t_render` 取样，不把模型瞬间贴到新点。

设 `p` 为当前模型水平位置，`q` 为新的取样点（已在序列上，含外推段与新点之间的线性段）。

1. `|p-q| ≤ 0.4m`：本帧直接跟 `q`（小于服务端「必须按移速走」的阈值）。
2. `0.4m < |p-q| ≤ catchup_snap_m`（建议 8m，需拍板）：本帧沿 `p→q` 走，步长 `min(|p-q|, move_speed * k_catchup * dt)`，`k_catchup` 建议 1.5。不回头：若 `dot(q-p, last_segment)` 明显为负且距离 < 2m，视为外推过冲，允许短反向贴回序列（这是校正不是掉头跑）；若为负且距离 ≥ 2m，先刹车停 1 帧再按新方向走，避免 180° 闪身。
3. `|p-q| > catchup_snap_m`：记一次瞬移指标，仍按 `move_speed` 走过去，**默认不 SetPosition 闪现**。只有开关 `aoiInterpAllowSnap`（默认关）且距离 > 快照阈值时才闪现（重连/全量视野，见 C.4）。

「落后太多」：`t_tip - t_render > delay_max + 0.15s` 时把 delay 下调 `slew_down`（更快追上序列），同时 Transform 仍受 3.3 的移速限制。这样追的是时间轴，不是瞬移。

### 3.3 位移 > 0.4m 与插值如何叠加

两层，不互相替代：

1. **路径层**（缓冲）：在延迟时间上算出希望到达的点 `q`。相邻采样水平距离 > 0.4m 时，这段路径的速度视为 `min(distance/dt_sample, move_speed)`，禁止把两点在一帧里当成已经到达。
2. **呈现层**（Transform）：`p` 走向 `q`，每帧步长 ≤ `move_speed * ClampRenderDelta(dt)`。大于 0.4m 的缺口一定是跑过去，不是跳过去。

`move_speed`：AOI 体上没有该字段。用 `MoveStepLimit.ServerMetersPerSec`（10）。以后若 ENTER/MOVE 带移速再换成字段，本次不引入。

Y 仍按当前 XZ 贴地（现有 `GroundY` + `FootClearance`），不在 Y 上外推。

---

## 4. C. 生命周期

### 4.1 ENTER

- 生成（或复用，见 4.3）模型。
- `buffer.Clear()`，只压入 ENTER 的 position。
- `delay = delay_default`，清空 EWMA。
- 模型放在 ENTER 点。一个点时不外推。
- `generation++`（见 4.3）。

### 4.2 LEAVE

优先级高于缓冲、外推、淡出、对账宽限。

同一帧、同一调用栈（`ApplyDelta` → `BodyRemoved` → `ReleaseView`）：

1. 丢掉该实体全部点序列、delay、EWMA、外推状态。
2. 关掉名字板，`SetActive(false)`。
3. `Destroy` 模型。
4. 从 `_views` / `AoiWorld` 去掉。

禁止：等下一条 MOVE、等 25s 静默、等 delay 播完、只改 alpha 不 Destroy。`aoiLeaveFadeSeconds` 不再用于离视野。若残留淡出代码被走到，下一帧必须 Destroy（现有 `Fading` 分支已如此）。

LEAVE 不走插值取样。

### 4.3 LEAVE 后很快 ENTER（边界抖动）

同一 `PlayerId`：

- 每个视图一个 `generation`（uint）。LEAVE 时 `generation++` 并标记 `deadGeneration`。
- 100ms 内 ENTER：
  - 若旧对象还在（Destroy 尚未落地）：取消「待销毁」语义，**复用**同一 GameObject，Clear 缓冲，只压入新 ENTER 点，瞬移到新点（这是进出窗口，不是走路）。
  - 若旧对象已没了：新建，缓冲从空开始。
- 旧环形缓冲不得并入新一代。用 generation 过滤任何晚到的、带旧 generation 的 Apply。
- 不在旧坐标上播残留点，因此不会闪一下旧姿势再跳到新 ENTER。

建议配置：`aoiInterpReenterReuseMs = 100`。

### 4.4 重连 / 全量视野

现有 `WorldSnapshotApplier.Apply` → `AoiWorld.ApplySnapshot(replace=true)` → `Clear()` 再 ENTER 列表。`Clear()` 今天不发 `BodyRemoved`。

实现阶段应改成：

1. `Clear()` 对每个 id 发 `BodyRemoved`（或返回被删 id 列表给 binder）。
2. binder 立刻删不在快照里的模型并清缓冲。
3. 快照里的人：已有视图则 **替换** 缓冲为快照点（generation++，一个点，不外推）；没有则当 ENTER。
4. 快照点与当前模型水平距离 > 8m 时允许一次贴齐（重连例外，计入瞬移指标）。
5. `keepExistingOnEmpty` / `NOT_READY` 现有保护保留：空快照不得清空周围人。

自己角色位置仍走现有快照 self / MoveRsp，不进远程缓冲。

---

## 5. D. 对账消息（只设计，不实现）

服务端将定期发「当前可见实体 ID 列表」。客户端尚未有该消息。协议字段本次不动。

建议语义（等你定稿）：

- 携带 `server_time_ms` 或 `server_seq`，表示这份名单对应的服务端时刻。
- 只删除，不创建。不在名单里且本地有模型 → 当 LEAVE：同一帧删模型、丢缓冲。计入 `reconcile_despawn`。
- 名单里有、本地没有 → 不补模，等 ENTER/快照。对账不是 ENTER。

与在途 ENTER/MOVE 的竞态：

1. **对账早于刚到的 ENTER**：ENTER 的 `server_time_ms`（或到达本地时间）≥ 对账时刻 → 本轮不对这个 id 删。宽限 `aoiReconcileGraceMs = 400`：对账之后、宽限内进表的 id 视为「名单过期」。
2. **ENTER 早于对账、对账没有它**：当真 LEAVE 处理（服务端认为已出窗口）。
3. **MOVE 在对账之后到达、id 已被对账删掉**：丢掉这条 MOVE，等新 ENTER。不把模型从缓冲里复活。
4. **对账与 LEAVE 重复**：第二次删除是空操作。
5. 开关 `aoiReconcileEnabled` 默认关；关时忽略该消息。

未验证：名单是全量还是增量、频率、是否含自己。这些要等服务端稿。实现阶段在 `ProtocolMapper` / `GameMeshClient.ApplyInnerPush` 加分支，不提前改 proto。

---

## 6. E. 调试与指标

开关关上时零开销（不写文件、不 Draw）。

每实体累计，观察者进程汇总：

| 指标 | 含义 |
|---|---|
| `interp_delay_ms` | 当前 delay（可直方图） |
| `buffer_count` | 点个数 |
| `underrun` | `t_render` 超过最新点的次数 |
| `coast_ms` | 外推累计时间 |
| `catchup` | 走 3.2 规则 2 的次数 |
| `teleport` | 走 3.2 规则 3 或快照贴齐的次数 |
| `leave_despawn` | LEAVE 删除 |
| `reconcile_despawn` | 对账删除（实现对账后才非 0） |
| `drop_old_seq` | 更旧 state_seq |

可视化（`aoiInterpDebugDraw`）：世界空间画缓冲折线、最新点、`t_render` 点、外推虚线。只画距离观察者最近的 N 个（默认 8），避免 300 人线框。可与现有 `Logs/aoi-stall-*.jsonl` 并存；插值打开后 `held_at_sample` 应明显下降。

---

## 7. F. 测试方案

新建 EditMode 测试（不依赖 Play Mode 编辑器锁）。时间用注入时钟，不靠 `Time.unscaledTime`。

1. **180ms 间隔**：20 个点，delay=200ms。每帧 `t_render` 落在开区间 (t0, t_tip)，水平速度变化不超过 10%。断言无「连续 ≥2 帧速度 < 0.05m/s 且尚未到路径终点」。
2. **600ms / 1s 间隔**：按你拍板的 delay 档。方案 A 允许走到点后的静止，但静止时长 ≤ `max(0, interval - delay - coast)` + 1 帧。方案 B/C 则要求无静止。
3. **一次 500ms 迟到**：第 5 个点晚 500ms。允许外推；新点到达后 `|p-q|` 在 2 帧内开始下降，无 >0.4m 的单帧水平跳变。
4. **LEAVE 同一帧**：Apply LEAVE 后，缓冲 Count=0，视图表无该 id，名字板对象 inactive。不需要再等 Update。
5. **LEAVE 后 100ms 内 ENTER**：新缓冲只有 ENTER 一个点，坐标是新点，旧点不在。
6. **乱序 / 重复 MOVE**：更旧 `state_seq` 丢弃；相同 seq + 新 position 接受；取样单调沿路径，不回到更早水平位置（外推过冲校正除外，测试里给明确阈值）。
7. **开关关**：行为与当前「追最新点并停下」一致。
8. **自己角色**：打开插值后，本地 `Transform` 仍不读远程缓冲（回归 `PlaceLocal` 不被 RemotePlayerView 驱动）。

「无瞬移」在测试里定义为：单帧水平位移 ≤ `move_speed * 0.05 + 0.01`，ENTER/LEAVE/快照贴齐除外。

---

## 8. G. 验证与回滚

### 8.1 开关前后怎么比

1. 同一台观察者、同一压测波次：先关开关录 60s，再开开关录 60s。看近处机器人是否还在点上停步。
2. 指标：`underrun`、`held_at_sample` 行数、`teleport`。近层 180ms 下，打开后 `held_at_sample` 应接近 0。
3. 出窗口：人还没跑到最后坐标，模型应已消失；打开插值不得把 LEAVE 推迟到 delay 之后。

### 8.2 回滚

1. 配置把上述开关保持默认 false（主回滚）。
2. git 回退本次插值提交（LEAVE 立即删除在更早提交里，不一起回滚）。
3. 不改服务器、不改协议，回滚客户端即可。

### 8.3 本次设计没验证的

- 真机弱网（丢包、RTT>200ms、到达乱序）下手感。500ms 抖动只在单测里模拟。
- 150–250ms 额外显示延迟对瞄准、近战、技能判定的影响。别人看到的你仍是服务端位置；你看到的别人晚 150–250ms。
- 远层 600ms–1s 在方案 A 下仍会停；未在线上远层频率上量过。
- 对账消息格式与频率（服务端未定）。
- 300 观察者同时开 debug draw 的开销（所以默认只画 8 个）。
- Unity 同帧 Destroy+Instantiate 在 IL2CPP 真机上的表现，只在编辑器逻辑上设计了复用。
- 远程没有 per-entity `move_speed` 时，10 m/s 是否和服务器每个人一致。

---

## 9. 建议改的文件（确认设计后再动）

实现插值 / LEAVE 加固（不含对账）：

| 文件 | 改什么 |
|---|---|
| `Assets/GameMesh/Runtime/Bootstrap/GameMeshClientConfig.cs` | 开关与 delay/coast/buffer 配置，默认关 |
| `Assets/GameMesh/Runtime/Aoi/RemotePoseBuffer.cs` | 环形序列、延迟取样、可选外推、generation、Clear |
| `Assets/GameMesh/Runtime/Player/GameMeshWorldBinder.cs` | Update 改走取样；LEAVE 仍同步销毁；同帧重入复用；呈现层 0.4m/移速 |
| `Assets/GameMesh/Runtime/Aoi/AoiModels.cs` | `Clear`/`PruneSilent` 也通知删除（快照不留幽灵） |
| `Assets/GameMesh/Runtime/Aoi/AoiInterpMetrics.cs` | **新建** 指标 |
| `Assets/GameMesh/Runtime/Aoi/AoiInterpDebugDraw.cs` | **新建** 可视化 |
| `Assets/GameMesh/Tests/EditMode/AoiInterpBufferTests.cs` | **新建** F 节用例 |
| `Assets/GameMesh/Tests/EditMode/AoiMapMailProtocolTests.cs` | LEAVE/同 seq 采样回归 |

对账（你确认协议后另开）：

| 文件 | 改什么 |
|---|---|
| `ProtocolMapper` / `GameMeshClient.ApplyInnerPush` | 解析名单（不改字段号的前提下） |
| `AoiWorld` + binder | 对账删除 + 宽限 |

不改：`LoadTestBot` 移动上行、心跳、`game.proto`、自己角色 `PlaceLocal`/`SendMoveAsync`。

---

## 10. 风险

1. 近层 150–250ms 延迟改变 PvP 手感（看到的别人更晚）。
2. 方案 A 下远层仍停步，看起来像没做插值。
3. 方案 B/C 远层最高约 700ms 延迟，非常明显。
4. 外推 300ms 在对方已停下时会多走出最多 3m（10 m/s × 0.3s），新 MOVE 再拉回，可能轻晃。
5. 同帧 LEAVE/ENTER 复用失败则闪一下空。
6. `Clear()` 若漏通知，全量快照会留下幽灵，直到 `SyncRemotes`；应在实现里补上。
7. 打开指标写盘在 300 人下可能卡 IO，指标默认关。
8. 现有 stall JSONL 的 `held_at_sample` 在外推期间可能误报，需要按「是否在外推」过滤。

---

## 11. 需要你拍板

1. **默认 delay**：200ms，还是 150 / 250？
2. **远层**：只 A（150–250 + 300ms 外推，1s 间隔仍可能停），还是 C（远层 delay 最高 700ms）？
3. **`aoiInterpFarGapMs`**：400ms 是否合适？
4. **外推上限**：300ms 是否合适？要不要默认就开 `aoiInterpCoastEnabled`（主开关仍默认关）？
5. **落后太多**：8m 内 1.5 倍移速追，8m 外仍跑不闪现？还是重连才允许闪现？
6. **远程 move_speed**：固定 10 m/s，还是以后用属性字段（本次不加字段）？
7. **LEAVE 同帧 ENTER**：复用 GameObject，还是 DestroyImmediate 再新建？
8. **对账宽限**：400ms？名单用 `server_seq` 还是 `server_time_ms`？
9. **主开关粒度**：一个 `aoiInterpEnabled` 打开延迟取样+外推，还是取样/外推分两个开关（上文按两个写）？

确认以上后再改代码。对账等协议稿再实现。
