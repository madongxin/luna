# 客户端源码热修说明

日期：2026-10-23。基线 `8f57f1f`。对照 `605_Source_Verified_Root_Cause_Audit.md` 里的客户端项。没有改服务器，没有应用 `WebServer_Parked_Move_200ms_Retry_Experimental.patch`。

## P0-CLI-01 主线程不再卡住 RPC 完成

`GameConnection.DispatchResponse` 在 `TryComplete` 成功后，直接在收包线程调用 `TrySetResult`。`TaskCompletionSource` 本来就是 `RunContinuationsAsynchronously`。

真正的 `ServerPush` 仍然放进 Unity dispatcher。碰场景物体的回调还在主线程。

这只去掉“回包已经到了，还要等 605 个机器人共用的 Update 队列”这一段。它不能保证服务器在 2 秒内把回包写出来。

## P0-CLI-02 迟到回包不再当成视野推送

`Late`、`Unmatched`、`GenerationMismatch`、`OpcodeMismatch` 只记计数，不调用 `PushReceived`。

机器人不再用 `GameResponse.Seq` 更新 `_lastServerSeq`。可靠序号只来自 `ServerPushEnvelope.ServerSeq`，而且要先通过连续序号检查。缺口不确认、不前进。空快照或被拒绝的快照不会把视野标成已恢复。

`client_seq=490` 和 `server_seq=362` 仍是两个不同的序号，不能相减。

## P1-CLI-03 入队和写出分开

移动计数现在是：计划、入队、写出、原始回包、匹配、晚到、超时、写出前超时、忙跳过、取消。

2 秒仍从请求入队时开始算，没有加长。超时或取消发生在写出之前时，发送循环丢掉这一帧，不再写到 socket。已经开始写的帧不会再发一次。

界面上的「发出」改成了「入队」和「写出」。每秒按网关汇总写出、匹配、晚到、超时，并记录 GC 次数、主线程队列长度、读循环解析峰值和帧时间峰值。这些数字要在一次实机里才有值。

## P1-CLI-04 空快照不是恢复成功

拒绝替换、更旧的 epoch、或推送缺口时，`AoiResyncHealthy` 为假。界面会显示「视野未恢复」。

只有快照通过实例、epoch 和基线并真正替换实体之后，或者缺口被连续事件补上之后，才把这个状态设回已恢复。

## 没有做的事

没有跑 605 人。没有读取 Gateway 的 `gamemesh_reply_soft_drop_total`。不能把这次超时全部归因于客户端队列，也不能说问题已经解决。
