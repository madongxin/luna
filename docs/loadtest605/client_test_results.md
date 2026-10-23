# 客户端测试结果

日期：2026-10-23。

## 已写的测试

`ConnectionAndPushTests`：

- `RpcResult_DoesNotNeedUnityDispatcherPump`：FakeGateway 正常回包，`QueueDispatcher` 一次都不 `Pump`，请求任务仍应在 2 秒内完成。
- `LateRpc_DoesNotBecomeServerPush`：回包故意晚于超时。`PushReceived` 次数应为 0，`PipeMetrics.Late` 至少为 1。

`LoadTest605V2Tests`：

- 超时之后不再写出。
- 已经开始写的请求，超时结果里 `FrameWritten` 为真。
- 机器人不用 RPC seq 推进可靠序号。序号 1 之后直接来 3，记缺口，可见实体不增加，`LastAppliedServerSeq` 停在 1。
- 已有可见实体时，空快照不标成已恢复，基线序号不变。带实体的快照可以替换，之后连续的 LEAVE 才减少人数。
- 另一张地图实例会清空可见集合并复位序号。

## 执行

本轮没有启动 Unity Test Runner。工程的 `Temp/UnityLockfile` 还在，说明编辑器正开着这个工程，batchmode 会抢工程，所以没有另开一次。

结果：**NOT RUN**。上面的用例不能写成已经通过。PlayMode 和 605 人实机也是 **NOT RUN**。
