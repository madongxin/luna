# 邮件奖励联调对账

客户端仓库：`https://github.com/madongxin/luna`。服务端仓库这次没有打开，也没有收到 `docs/mail_reward_integration_contract.md`。

下面没有一条是两边已经共同确认的测试数据。不能在测试库里按这些空行复现。

| 场景 | 角色 | mail_id | operation_id / trace_id | 领取前 asset_version | 领取后 asset_version | 道具变化 | 事务流水 | 结论 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| REWARD-01 在线收件 | 未分配 | — | — | — | — | — | — | 待服务端提供角色 A |
| REWARD-02 离线收件 | 未分配 | — | — | — | — | — | — | 待服务端提供角色 B |
| REWARD-03 单领到账 | — | — | — | — | — | — | — | 待确认。客户端只在版本前进后显示「已到账」 |
| REWARD-04 同键重试 | — | — | 原键 | — | — | — | — | 待确认。客户端不换键 |
| REWARD-07 超时重试 | — | — | 原键 | — | — | — | — | 待确认 |
| REWARD-08 同步失败后的快照 | — | — | 原键 | — | — | — | — | 待确认 |
| REWARD-13 批领全部失败 | — | — | 同一组前缀 | — | — | — | — | 待确认。客户端保留逐封错误 |

复现步骤要等服务端给出：

1. 隔离库和两个测试角色的 `player_id`。
2. 一封带 `ITEM` 附件的 SYSTEM 邮件，以及对应 `mail_id`。
3. Gateway 地址，通过环境变量 `GAMEMESH_E2E_GATEWAY` 注入，不写进客户端。
4. 领取前后各一份 `FullStateSnapshotRsp`，带 `asset_version`、`item_ids`、`item_counts`。
5. 服务端资产流水里的同一 `trace_id`（客户端把它设成这次 `idempotency_key`）。

对不上的数量保持「待确认」，不用界面文案代替流水。
