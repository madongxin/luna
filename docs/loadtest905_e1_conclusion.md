# 905 压测 E1 结论（客户端）

范围：只加统计/导出/测试，不改协议，不改 2s RequestAsync 超时。缺失写「无数据」。

## 导出文件（压测结束点「导出移动统计」）

| 文件 | 内容 |
|---|---|
| `Logs/movement-metrics-*.json` | 聚合：循环类型、Sparse/Cluster/Hz/段长、重发、RTT 分位、duty、AOI 观察计数 |
| `Logs/movement-metrics-*.csv` | 同上扁平行 |
| `Logs/movement-bots-*.csv` | 每机器人一行 |
| `Logs/aoi-visible-*.json` | `observer_player_id → entity_ids`（BotPushTracker 可见表，供 `/aoi/visible` 对账） |

19:20 那一轮：压测机不可达 → **参数与导出文件：无数据**。

## 推断条目

| ID | 结论 | 标记 |
|---|---|---|
| I1 | `LoadTestBot` 在 2s 未收到 MOVE 权威后通过 `BotResendPolicy`/`TryTakeResend` 原地重发同一目标；RTT>2s 时会放大上行。 | **测试证实**（`E1_ReplyDelayOverTwoSeconds_TriggersSameTargetResend`：3s 延迟路径下连续重发计数上升） |
| I2 | Dense 循环 `MaxInflight=1`，拥塞时实际上报 ≈ 1/RTT；与服务端观测的每机器人 ~0.8 帧/s（RTT≈1.25s）量级吻合。 | **代码推断**（`DenseMoveLoopAsync` + `AwaitingMoveAuthority`）；19:20 是否走 Dense：**无数据**（需导出 `loop_kind`） |
| I3 | 默认 `SparsePatrol=true`、`ClusterSize=60`；`ConfigureMovement` 与旧 `movement_profile.sample.json` 未改这两项。面板现已显示实际循环。 | **代码推断**；19:20 面板是否改过：**无数据** |

## 关于「0.8/s 来自哪里」

1. **若导出 `loop_kind=Dense`**：优先解释为稠密循环被 RTT 压到 ~1/RTT（代码推断 + 与服务端帧率量级一致）。
2. **若导出 `loop_kind=Sparse`**：基线段发约 0.05/s，心跳约 0.1/s，远小于 0.8；则 0.8 更可能来自 **重发风暴 / 其它上行**，需看导出里的 `move_resend`、`move_timeout`、`actual_send_hz`（本轮无数据）。
3. 仓库默认是 Sparse，因此「默认配置下应是 Sparse」是代码推断；不能在缺少导出时断言 19:20 一定是 Dense。

## E2 开关（已实现，默认关）

- `aoi_bot_backoff`（`BotMovementSettings.AoiBotBackoff`，面板可勾选）
- 打开后：同目标重发指数退避（2/4/8… 上限 30s），连续 N 次（默认 3）后换路点；10s 窗超时比例>30% 时段长×2（最多 4×）
- 测试：`E2_Backoff_CutsResendsBelowOneFifthOfImmediateStorm`、`E2_CongestionGate_ScalesSegmentsThenRecovers`

## 未验证

- 真机弱网手感、905 全量导出对账
- 服务端时钟外推稳定性（见 E3 设计，未改代码）
