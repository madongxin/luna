# 压测机器人心跳失败审计（Q4，只读统计）

不改心跳逻辑、不改 2s Move 超时、不改协议。

## 统计字段（导出 json）

| 字段 | 含义 |
|---|---|
| `heartbeat_ok` / `heartbeat_fail` | 成功 / 失败总数 |
| `heartbeat_fail_timeout` | `ClientTimeout`（TCP 仍可能活着） |
| `heartbeat_fail_disconnect` | 链路断开 / 传输死亡 → 会走 `TryResumeAsync` |
| `heartbeat_fail_reject` | 带 `ERR_*` 的服务端拒绝类 |
| `heartbeat_fail_other` | 其余 |
| `heartbeat_reconnects` | 因心跳路径触发的重连次数 |
| `heartbeat_per_minute` | `[{m,ok,fail},…]` 按分钟 |

## 对 21% 失败的含义（基线 F4）

| 问题 | 结论 | 标记 |
|---|---|---|
| 失败是否等于被服务端 12s 心跳超时踢掉？ | **否，不能等同**。客户端在 TCP 仍连通时把慢回包记为 `timeout` 并继续 Move；只有 `disconnect` 才立即重连。服务端 12s 空闲踢人需看服务端会话日志；本客户端导出**无数据**证明是否被踢。 | **代码推断** |
| 21% fail 时重连多少？ | 看新导出 `heartbeat_reconnects` 与 `heartbeat_fail_disconnect`；旧基线未分原因 → **无数据**。 | 待压测 |

## 客户端行为（未改）

1. 心跳 RPC 预算约 12s（`RpcBudget(12000)`）。
2. 超时：`HeartbeatFail++`，短暂停顿后继续，**不**停 Move。
3. 链路断：记 disconnect + reconnect，再 `TryResumeAsync`。
4. Hello 下发的 `HeartbeatIntervalMs` 决定间隔（常见 5–15s）。

## 建议核对

下一轮导出后对比：`fail_timeout` 占比、`reconnects`、服务端 session kick 计数。三者对不上时，优先信服务端踢人日志。
