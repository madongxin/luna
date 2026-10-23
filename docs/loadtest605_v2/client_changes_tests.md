# 改动和测试

## 代码

- `Assets/GameMesh/Runtime/Network/RpcPendingTable.cs`：待响应表、守恒计数。
- `Assets/GameMesh/Runtime/Network/GameConnection.cs`：用这张表匹配回包；记录写出的帧、解码帧、解包耗时。
- `Assets/GameMesh/Runtime/LoadTest/LoadTestBot.cs`：连续移动超时后暂停并拉快照；过载退避；心跳退避；重连抖动；进图记录实例和线。
- `Assets/GameMesh/Runtime/LoadTest/LoadTestRunner.cs`：全部机器人的实例分布和状态合计。
- `Assets/GameMesh/Runtime/LoadTest/BotMovementMetrics.cs`：过载、权威刷新、退避时长。
- `Assets/GameMesh/Runtime/Protocol/WorldSnapshotApplier.cs`：空快照、`NOT_READY`、旧 epoch。
- `Assets/GameMesh/Runtime/Bootstrap/GameMeshClient.cs`：保留非空视野；`PushAck` 的重同步错误会触发一次后台快照；日志写出 wire、parsed、aoi、views。
- `Assets/GameMesh/Runtime/UI/GameMeshRuntimeUi.cs`：请求 seq 和推送 server_seq 分开写；分线分布；快照计数。
- `Assets/GameMesh/Tests/EditMode/LoadTest605V2Tests.cs`：不连网关的 EditMode 测试。

## 测试覆盖

- 正常完成、晚到、超时和收包只有一个赢家、旧 generation、opcode 不匹配。
- `72096 = 34103 + 0 + 37580 + 413`。
- 第一次移动超时不进入暂停；第二次起暂停，封顶 15 秒。
- 心跳暂停从 5 秒加倍到 30 秒，和移动暂停不是同一个数。
- 空快照不替换已有实体；`NOT_READY` 不替换；旧 epoch 拒绝；不同实例不相等。
- 推送出现缺口后，快照基线重置，之后的连续序号可以应用，基线之前的序号是重复。
- 待响应匹配不需要主线程。

## 未跑

EditMode 这批测试和全部实机阶段都是 **NOT RUN**。编辑器开着工程时没有启动第二个 Unity 去跑测试。模拟出来的回包不能写成实机结果。

退出当前 Play 后，下一次进 Play 才会加载这批脚本。
