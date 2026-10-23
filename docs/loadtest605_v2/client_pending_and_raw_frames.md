# RPC 待响应和原始帧

## 键

`RpcPendingTable` 的键是 `(connectionGeneration, GameRequest.Seq)`。

`GameRequest.Seq` 由这条连接上的 `AssignRequestSeq` 分配。它不是移动状态序号，也不是推送 `server_seq`。

期望的响应 opcode 可以记在槽上。线上发送时这个值是 0，因为请求 oneof 和响应 oneof 的数字不是同一套，不能直接比较。测试可以传入非 0 的期望 opcode，错 opcode 不会完成这次调用。

## 完成权

超时回调和收包回调用 `Interlocked.CompareExchange` 抢同一个槽。只有一个能从 `Open` 改走。

- 超时先赢：计入 `Timeout`。之后同键的帧计入 `Late`，不完成 `Task`，不把坐标当成权威。
- 收包先赢：计入 `AckOk` 或 `ExplicitReject`。之后的超时回调失败，不再计入超时。
- 旧 generation 的序号碰到新 generation 上还开着的同序号：计入 `GenerationMismatch`，不完成新请求。
- 连接失败把仍开着的槽收成 `OtherTerminal`。

## 守恒

同一窗口、不重复计数：

`sent = ack_ok + explicit_reject + timeout + cancelled + still_pending + other_terminal`

2026-10-23 那次截图：

`72096 = 34103 + 0 + 37580 + 0 + 413 + 0`

413 是截图当时还在途、还没返回、也还没到 2 秒超时的请求。这是用上面的式子从已有计数推出来的，不是新跑出来的一类测量。前一次截图的差额也是 413，原因相同：每个在途 Move 最多占一个名额，当时大约有 413 个机器人正卡在未完成的那一次发送上。

## 计数

每条连接的 `PipeMetrics` 有：已解码帧、未知 opcode、待响应命中、超时、晚到、序号对不上、generation 不匹配、opcode 不匹配、读循环里解包耗时的峰值。

解包耗时只量 `ReadAsync` 返回之后的解析，不把等包的空闲时间算进去。

## 线程

600 条机器人连接没有共用一个收包锁。每条连接自己 `ReadAsync` / `WriteAsync`。

完成回调会进 Unity 主线程。超时已经在那之前判定，所以主线程排队不会把一次来得及的回包改记成 `CLIENT_TIMEOUT`。

心跳和移动是各自的循环。心跳超时只拉长下一次心跳的间隔（5 秒起，加倍，封顶 30 秒，再加按机器人序号的抖动），不会因为心跳超时去重连。只有链路已经断了才重连，并且按序号错开，避免同时重连。
