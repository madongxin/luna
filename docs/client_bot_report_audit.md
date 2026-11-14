# 客户端压测上报审计（Q1）

范围：只加统计/测试/文档，不改稀疏上报默认行为。基线导出：`client-20261113-221629`、复测 `client-20261113-231200`。F5/占比实测定稿：`client-20261114-130208`（`Logs/export-20261114-130208`）。

## 0.7–0.77 次/秒由什么组成

| 成分 | 量级（基线 KeepInside=32） | 依据 |
|---|---|---|
| **短段 depart** | 主导：KeepInside(32) 把腿压到 ≤31m，`_segmentSec≈距离/10` → **约 3–5s/段**，不是配置的 10–30s；连续 Sparse 无停留 → 约 **0.2–0.3 depart/s/机器人** | **测试证实** `Q1_I1_KeepInside32_SegmentDuration_P50_IsAbout3to5Sec_NotConfigured10to30` |
| **arrive** | 确认位未到终点时段时钟到期会再发 arrive；拥塞时确认更慢 → arrive 增多 | **测试证实** `Q1_I2_SlowConfirm_ProducesArriveAndResendBaseline`；**导出证实** `130208`：`send_arrive/move_sent ≈ 1.19%` |
| **resend** | 基线 `move_resend/move_sent ≈ 15–17%`；`aoi_bot_backoff=false` 时 2s 超时即原地重发 | **代码推断**（导出 F2）+ **测试证实** `Q1_I4`；**导出证实** `130208`：`16.96%` |
| **心跳** | 约 0.07–0.1/s，远小于 0.7 | **代码推断**（间隔 5–15s）；`130208`：`heartbeat_ok=8482` / ~7min ≪ move |

合计与实测 ~0.7–0.77/s 同量级（基线半径 32）：**短段连续 depart + arrive/resend 放大**，不是配置上的 10–30s 稀疏。

`130208`（`aoi_radius_m=96`）：`bot_send_hz_p50≈0.82–0.98`；**规划** `segment_sec_p50=9.5`、`segment_m_p50=95`——但 **实际 depart 间隔**跟 RTT 走（见 J1），不是规划 9.5s。总上报仍主要由 **depart(81.85%) + resend(16.96%)** 组成。

## I1 / J1（R1 修正）

| ID | 结论 | 标记 |
|---|---|---|
| **I1（规划段长）** | KeepInside + `_segmentSec=距离/ServerMoveSpeed` 决定 **规划** 段长；半径 32 → 规划 p50≈3–5s；半径 96 → 规划 p50≈9.5s。`segment_sec_*` / `planned_segment_sec_*` 导出的是这个。 | **测试证实** `Q1_I1_*` + **导出证实** `130208` |
| **J1（实际配速）** | **成立**。服务端对较长路点把权威直接设为终点并回包；机器人采纳后 `Flat(confirmed,_goal)≤ArriveEpsilon` → `CompleteSegment(arrived_by_confirm)` → 同一次 Poll 立刻下一段 depart。稀疏退化为 **回包一到就发下一个**，depart 间隔 ≈ RTT（+固定开销），不是 `SegmentMinSec`。 | **测试证实** `R1_J1_InstantConfirmToGoal_DepartIntervalTracksReplyDelay_NotSegmentMin`（50/300/1000ms） |
| **J2** | `segment_sec` 是 PickSegment 规划值；实际墙钟用 `actual_depart_interval_ms_*`（depart→depart）。 | **代码** R1 |
| **J3** | RTT 变好后上报会跟着升（上限约 1/最小开销）；需 R2 绝对间隔上限（等 J1 live 再开）。 | **代码推断** |

## I2–I4

| ID | 结论 | 标记 |
|---|---|---|
| **I2** | **成立**。确认位停滞时 `NeedArrive` 会发 arrive；可与 resend 叠加。 | **测试证实**；live arrive 占比见上 |
| **I3** | **成立**。`MoveDutyPct=50` 时稀疏仍几乎不记 `DwellingMs`，duty≈100%。默认 `sparse_duty_pct=100` 时 live `dwelling_ms=0`、`duty=1`。 | **测试证实** + **导出证实** `130208` |
| **I4** | **成立（机制）**。`aoi_bot_backoff` 已接到稀疏 `BotResendPolicy`；`130208` 仍为 **false**，故未生效。 | **测试证实** + **导出证实** |

→ I1–I4 / J1 均成立（EditMode）；**R2 等本轮只部署 R1 的压测用 `actual_depart_interval` / `segment_complete_*` 再 live 证实 J1**。

## F5（AOI 导出为空）— 已修好（live）

**原因（代码）**：`BotPushTracker.ObserveServerPush` 在 **server_seq 缺口** 时直接 `return false` 且不 `ApplyDelta`；另：`LoadTestBot.OnPush` 曾忽略裸 `AoiDelta`。

**Q1 修复（只改统计）**：缺口/不可靠帧仍 `ApplyDelta` 计数，不推进可靠游标；裸 `AoiDelta` 走 `ObserveBareAoiDelta`。

**`130208` 导出证实**（不再为空）：

| 字段 | 值 |
|---|---|
| `aoi_move_events` | 864251 |
| `aoi_visible_peak` | 9 |
| `aoi_visible_p50 / p95 / max` | 2 / 4 / 7 |
| `aoi_gap_skipped` | 2 |
| `aoi_bare_frames` | 609437 |
| 分文件 | `aoi-visible-20261114-130208.json`（每观察者 `entity_ids` + `visible`/`visible_peak`） |

观察者可见人数整体偏低（p50=2）：与 Sparse + 簇间距 96m + 稀疏上行一致，**不是**计数器仍为 0。

## R1 统计修正（行为不变）

- `planned_segment_sec_*`（=原 `segment_sec_*`）与 `actual_depart_interval_ms_*` 并排导出
- `segment_complete_{arrived_by_confirm,timer_expired,rejected,other}`
- `moving_time_ratio`：MovingMs÷样本墙钟，钳制 [0,1]；汇总用各 bot 均值（修 F6=454）
- `movement-bots` CSV 去掉重复 `loop_kind`；每 bot 含 depart 间隔分位

## R3 压测机指标

导出：`host_cpu_pct_*`（`Process.TotalProcessorTime`）、`host_stall_ms_max` / `host_pool_queue_ms_max`、`host_frame_ms_*`、`host_gc_gen*`、`move_start_to_write_ms_*`、`move_recv_to_callback_ms_*`、`build_sha` / `started_at` / `ended_at`。

**RPC p50≈1.17s 里客户端占多少**：用下次 R1 导出拆：
- `move_start_to_write_ms_p50` = 排队+写出（客户端）
- `move_recv_to_callback_ms_p50` = 收齐匹配→await 续体（客户端，通常很小）
- `rpc_latency_p50` = write_completed→MoveRsp（含网络+服务端；**不是**纯网关 100ms）
- 客户端合计 ≈ start_to_write + recv_to_callback；其余为网线+服务端处理

**精确 GC pause ms**：运行时 API 无直接暂停时长 → 导出写 `无数据` 口径，用 `host_stall_ms_max`（进程停顿探针）+ gen 计数代替。

## 后续任务状态

| 任务 | 状态 | 默认 |
|---|---|---|
| Q2–Q4 | 已合入 | duty 100；arrive 门控关；backoff 关 |
| R1 统计 | 本次 | 只统计，行为不变 |
| R3 主机指标 | 本次 | 只统计 |
| R2 绝对间隔 / arrive_by_clock | **等 R1 压测证实 J1** | 默认保持现有 |

## Bot230 / duplicate_target 站桩（2026-11-14）

服务端：到点后 `unchanged` 约每 2s 一次。客户端根因：迟到 MoveRsp 在 **state_seq 未递增**（unchanged）时清了重发闩却 **不更新 `_pos`**。已修：`ShouldAdoptLatePose`。

## 未验证

- **R1 真机**：`actual_depart_interval_ms_p50` 是否 ≈ 本机 RTT；`segment_complete_arrived_by_confirm` 是否占绝大多数
- R2 开启后绝对速率上限与占空比 60%
- 机器人放慢后观察者可见人数
- 完全无回包时依赖 `aoi_bot_backoff` 放弃目标
