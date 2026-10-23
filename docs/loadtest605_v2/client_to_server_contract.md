# 客户端交给服务器的约束

仓库里没有 `server_to_client_contract.md`。这里只写客户端现在依赖的、协议里已经有的行为。没有新字段。

## 移动

- `MoveRsp.ok=true` 只表示权威坐标已提交，不表示别人已经看见。
- 一步不超过 `10 m/s * dt`。`dt` 来自客户端这次和上次的 `client_time_ms`。
- 2/5/10Hz 只是客户端发送节奏，不是服务器拒包上限。
- 别人看见的位置大约每 200ms 合并一次。
- 超时不是拒绝。客户端在连续超时后会停发，并用 `WorldSnapshotReq` 读取自己的权威坐标。服务器应在这份快照的 `self.position` 里给出已提交的位置。
- `ERR_OVERLOADED` 表示过载，客户端会退避。如果出现 `ERR_BUSY`，同样退避。

## 心跳和 RPC

- 心跳和移动是独立请求，各自有 `GameRequest.Seq`。
- 回包必须带回同一个 `GameResponse.Seq`。这个 seq 不是推送 `server_seq`。
- 晚于客户端超时到达的回包不会再改这次调用。服务器仍可能已经提交了那一步。
- 心跳超时本身不应要求客户端拆掉一条仍然活着的 TCP。

## 视野

- `PushAck` 的 `ERR_AOI_RESYNC_REQUIRED` 表示需要全量快照，不是已经恢复。
- 快照要带本会话的 `map_instance_id`、`owner_epoch`、`baseline_server_seq`。
- `ok=false`、`NOT_READY`、`NOT_ON_MAP` 不能被客户端当成「周围没人」。
- `ok=true` 且实体列表为空，只有在这个观察者周围确实没人时才成立。观察者实例上已知有其他人时，空列表是不一致。
- 每个观察者最多看见 200 个其他玩家，自己不计入。
- 不同线、不同实例之间没有 AOI。

## 分线

- 1002：每线最多 400，最多 8 线，合计 3200。600 人应分到多条线。
- 软上限由进图或分线回包给出。客户端不把 100 当成硬上限，也不自行放宽。
- 客户端要能从进图回包读到 `map_instance_id`、`line_no`、`gamelogic_instance_id`。`gamelogic_instance_id` 只用于诊断。
