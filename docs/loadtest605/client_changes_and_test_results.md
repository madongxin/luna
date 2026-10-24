# 改动和测试

## 代码

- `RpcPendingTable`：发送前检查截止和槽状态；写开始和写完成分开；超时表不再整表清空。
- `GameConnection`：一帧一次唤醒；队列年龄；迟到的普通回包走 `LateResponse`，仍然不进视野推送。
- `LoadTestBot`：队列堵住时不制造新 Move；写出前过期不停步；写出后未知才停；连续 3 次未知才快照。
- `MoveRecovery`：更晚的 `state_seq`、同一 generation、同一地图才采纳迟到坐标。
- 超时日志只保留前 5 条完整栈，之后每 10 秒一行合计。

## 已写的 EditMode

- 截止已过的帧不会进入写开始。
- 超时之后的槽不能再写。
- 已经开始写的超时记在 `timeout_after_write_started`，和写完成不是同一个数。
- 写出前过期次数不到 3 不会要快照。
- 更晚的 `state_seq` 才能恢复；别的 generation 或别的地图不能。

## 执行

工程上有 `Temp/UnityLockfile` 时没有另开 Unity Test Runner。这些测试是 **NOT RUN**。C7 的多进程对比、C8 的 10–15 分钟 2Hz/5Hz 实机也是 **NOT RUN**。不能标成通过，也不能说 12 秒队列已经消失。
