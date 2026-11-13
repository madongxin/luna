# 客户端压测上报审计（Q1）

范围：只加统计/测试/文档，不改稀疏上报默认行为。基线导出：`client-20261113-221629`、复测 `client-20261113-231200`。

## 0.7–0.77 次/秒由什么组成

| 成分 | 量级（基线） | 依据 |
|---|---|---|
| **短段 depart** | 主导：KeepInside(32) 把腿压到 ≤31m，`_segmentSec≈距离/10` → **约 3–5s/段**，不是配置的 10–30s；连续 Sparse 无停留 → 约 **0.2–0.3 depart/s/机器人** | **测试证实** `Q1_I1_KeepInside32_SegmentDuration_P50_IsAbout3to5Sec_NotConfigured10to30` |
| **arrive** | 确认位未到终点时段时钟到期会再发 arrive；拥塞时确认更慢 → arrive 增多 | **测试证实** `Q1_I2_SlowConfirm_ProducesArriveAndResendBaseline`；live 占比需看新导出 `send_arrive` |
| **resend** | 基线 `move_resend/move_sent ≈ 15–17%`；`aoi_bot_backoff=false` 时 2s 超时即原地重发 | **代码推断**（导出 F2）+ **测试证实** 稀疏路径可用同一 `BotResendPolicy`（`Q1_I4`） |
| **心跳** | 约 0.07–0.1/s，远小于 0.7 | **代码推断**（间隔 5–15s） |

合计与实测 ~0.7–0.77/s 同量级：**短段连续 depart + arrive/resend 放大**，不是配置上的 10–30s 稀疏。

## I1–I4

| ID | 结论 | 标记 |
|---|---|---|
| **I1** | **成立**。`KeepInside` + `_segmentSec=距离/ServerMoveSpeed` 使 p50 段长约 3–5s，与 `SegmentMinSec=10` 不符。 | **测试证实** |
| **I2** | **成立**。确认位停滞时 `NeedArrive` 会发 arrive；可与 resend 叠加。 | **测试证实** |
| **I3** | **成立**。`MoveDutyPct=50` 时稀疏仍几乎不记 `DwellingMs`，duty≈100%。 | **测试证实** |
| **I4** | **成立（机制）**。`aoi_bot_backoff` 已接到稀疏循环的 `BotResendPolicy`；两轮压测均为 **false**，故未生效。打开后稀疏同样退避。 | **测试证实**（策略）+ **导出证实**（两轮均为 false） |

→ I1–I4 均成立，**可以进入 Q2**（占空比）。不要指望靠加长 KeepInside 内路段回到 10–30s 上报间隔。

## F5（AOI 导出为空）

**原因（代码）**：`BotPushTracker.ObserveServerPush` 在 **server_seq 缺口** 时直接 `return false` 且不 `ApplyDelta`；905 压测极易缺口，计数与可见表一直为 0。另：`LoadTestBot.OnPush` 忽略裸 `AoiDelta`（只处理 `ServerPush`）。

**Q1 修复（只改统计）**：缺口/不可靠帧仍 `ApplyDelta` 计数，不推进可靠游标；裸 `AoiDelta` 走 `ObserveBareAoiDelta`。导出增加 enter/leave/move/visible 分位数；`move_sent=0` 的空包拒绝保存。

## 后续任务状态

| 任务 | 提交 | 默认 |
|---|---|---|
| Q2 `sparse_duty_pct` | 已合入 | 100（连续） |
| Q3 `sparse_arrive_wait` + 稀疏退避 | 已合入 | arrive 门控关；`aoi_bot_backoff` 关 |
| Q4 心跳分原因 | 见 `client_bot_heartbeat_audit.md` | 只统计 |

## 未验证

- 真机再跑一轮后 `send_depart/arrive/resend` 精确占比
- 缺口统计修复后 `aoi_visible_*` 是否非零（需新压测）
- 真实玩家占空比与压测 60% 目标的差距
- 机器人放慢后是否仍覆盖足够多观察者
