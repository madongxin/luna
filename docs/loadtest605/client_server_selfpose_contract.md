# 自己坐标查询

当前协议没有只读的自己坐标请求。`MapPingRsp` 只有人数，没有位置。`MoveRsp` 有 `position`、`yaw`、`state_seq`，所以迟到的移动回包可以修正自己，但必须是更新的 `state_seq`。`WorldSnapshot` 会带整份视野，不适合每次移动未知都发一次。

如果服务器要加一个轻量查询，客户端会按这些已有概念来用，不会自己编字段进 `Game.cs`：

- 只查自己，要带当前 session。
- 返回 `map_instance_id`、`state_seq`、`position`、`yaw`。
- `state_seq` 不比客户端已经确认的更新，就忽略。
- 别的地图实例的结果忽略。
- 它不是视野恢复。视野缺口仍走现有的 `WorldSnapshot`。

在这个接口出现之前，客户端只用迟到的 `MoveRsp`，并在连续 3 次「已经开始写但没有回包」之后才退回一次全量快照。
