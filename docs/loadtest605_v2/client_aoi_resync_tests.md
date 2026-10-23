# AOI 重同步

## 状态

已有的单飞标记 `_snapshotInFlight` 仍在。同一会话不会叠两次 `WorldSnapshot`。

`PushAck` 返回 `ERR_AOI_RESYNC_REQUIRED` 只说明需要全量同步。客户端会显示这个错误，并发起一次后台快照。这个回包本身不是恢复完成。

## 什么时候替换实体

`WorldSnapshotApplier.ShouldReplaceEntities`：

- 快照不成功：不替换。
- `NOT_READY` 或 `NOT_ON_MAP`：不替换。
- 线上实体数为 0，而本地已经有其他人：不替换。空列表不能把已知的非空视野清掉。
- 本地本来就没有其他人，且快照有效：允许应用空列表。
- `owner_epoch` 比当前更旧：整张快照拒绝，不清实体。
- 地图实例不一致：原有 `TryBuild` 仍返回 `ERR_MAP_DATA_MISMATCH`，不应用。

留下实体时仍可以更新自己的坐标。推送序号基线不在这次被拒绝的空快照上重置。

## 日志和界面

应用或保留时记录：

- `wire`：`AoiEntities` 的个数
- `parsed`：解析后的个数
- `aoi`：`Aoi.Entities.Count`
- `views`：`RemotePlayerView` 的个数

界面上玩家行显示「快照线/解析/保留」。

## 机器人快照

连续两次 Move 超时后，机器人把位姿标成未确认，停止从旧坐标接着走，并用已有的 `WorldSnapshotReq`（`player_id`、`last_applied_server_seq`）拉权威坐标。同时最多 4 个这样的快照，并按机器人序号抖动。快照失败、`NOT_READY`、`NOT_ON_MAP` 或没有自己的坐标时，不改本地坐标，继续暂停。

`ERR_OVERLOADED` 和 `ERR_BUSY` 单独计入过载，并走同一套暂停。`ERR_BUSY` 不在错误目录里；如果回包带了这个字符串，客户端会认，没有把它写进 proto。

## 测试

EditMode 覆盖：空快照不替换非空集合、`NOT_READY` 不替换、有效空集合在本地为 0 时可以应用、旧 epoch 拒绝、别的实例不相等、推送缺口之后用快照基线再收连续 delta。

这些测试没有连网关。实机 1+1、同实例密视野：**NOT RUN**。
