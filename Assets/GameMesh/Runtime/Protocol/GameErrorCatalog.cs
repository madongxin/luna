using System.Collections.Generic;
using GameMesh.Network;

namespace GameMesh.Protocol
{
    public readonly struct GameErrorInfo
    {
        public readonly string Code;
        public readonly string Chinese;
        public readonly bool Retryable;
        public readonly string Detail;

        public GameErrorInfo(string code, string chinese, bool retryable, string detail = "")
        {
            Code = code ?? "";
            Chinese = chinese ?? "";
            Retryable = retryable;
            Detail = string.IsNullOrEmpty(detail) ? (chinese ?? "") : detail;
        }
    }

    public static class GameErrorCatalog
    {
        static readonly Dictionary<string, GameErrorInfo> Map = new Dictionary<string, GameErrorInfo>
        {
            [GameMeshErrorCode.ClientNotConnected] = new GameErrorInfo(GameMeshErrorCode.ClientNotConnected, "尚未连接到服务器", true,
                "还没连上 Gateway。请确认服务器地址/端口后重新点注册或登录。"),
            [GameMeshErrorCode.ClientIllegalState] = new GameErrorInfo(GameMeshErrorCode.ClientIllegalState, "当前状态不允许该操作", false,
                "当前连接状态不允许这次操作。若刚点过登出，请等断开完成后再登录。"),
            [GameMeshErrorCode.ClientTimeout] = new GameErrorInfo(GameMeshErrorCode.ClientTimeout, "请求超时", true,
                "服务器没有在时限内回包。可稍后重试；连续超时请检查网络或服务器是否还在跑。"),
            [GameMeshErrorCode.ClientCancelled] = new GameErrorInfo(GameMeshErrorCode.ClientCancelled, "请求已取消", true,
                "这次请求被取消了。可以再点一次。"),
            [GameMeshErrorCode.ClientProtocol] = new GameErrorInfo(GameMeshErrorCode.ClientProtocol, "协议解析失败", false,
                "收到的数据包无法解析。请确认客户端协议与服务器一致后重进。"),
            [GameMeshErrorCode.ClientQueueFull] = new GameErrorInfo(GameMeshErrorCode.ClientQueueFull, "请求过多，请稍后重试", true,
                "本地待发送请求堆积。稍等一两秒再点，不要连点。"),
            [GameMeshErrorCode.ClientDisconnected] = new GameErrorInfo(GameMeshErrorCode.ClientDisconnected, "连接已断开", true,
                "TCP 已断开。关面板不会自动登出；再操作会重新握手。请重新点登录。"),
            [GameMeshErrorCode.ClientInvalidCoord] = new GameErrorInfo(GameMeshErrorCode.ClientInvalidCoord, "坐标非法", false,
                "提交的坐标不合法。请回到服务器给的位置后再移动。"),
            [GameMeshErrorCode.MapHashMismatch] = new GameErrorInfo(GameMeshErrorCode.MapHashMismatch, "本地地图与服务器不一致", false,
                "本地地图资源和服务器不一致。请更新地图数据后再进图。"),
            [GameMeshErrorCode.ProtocolMissing] = new GameErrorInfo(GameMeshErrorCode.ProtocolMissing, "当前协议缺少所需类型", false,
                "当前导入的协议缺少这个接口。请用服务器导出的 game.proto 重新生成协议。"),
            [GameMeshErrorCode.ServerError] = new GameErrorInfo(GameMeshErrorCode.ServerError, "服务器返回错误", true,
                "服务器拒绝了这次请求，但没有给出标准错误码。请看下面的原因，按提示改完再试。"),
            ["ERR_INVALID_ARGUMENT"] = new GameErrorInfo("ERR_INVALID_ARGUMENT", "请求参数非法", false,
                "常见原因：设备 ID 为空，或密码为空/不足 6 位。注册和登录都必须带同一份密码，不要空着点按钮。"),
            ["ERR_UNAUTHENTICATED"] = new GameErrorInfo("ERR_UNAUTHENTICATED", "未完成握手或未登录", false,
                "还没完成 Hello 或尚未登录。请等 Hello 显示 OK 后再点注册/登录。"),
            ["ERR_FENCE_STALE"] = new GameErrorInfo("ERR_FENCE_STALE", "会话栅栏已过期", false,
                "会话令牌已失效。请重新登录拿新 token，不要沿用旧连接上的操作。"),
            ["ERR_SESSION_EXPIRED"] = new GameErrorInfo("ERR_SESSION_EXPIRED", "会话已过期，请重新登录", false,
                "会话不存在或宽限期已过。请填密码重新登录。"),
            [GameMeshErrorCode.SessionReplaced] = new GameErrorInfo(GameMeshErrorCode.SessionReplaced, "账号已在其他设备登录", false,
                "这个账号在别处登录，本端已被顶号。若要继续玩，请重新登录。"),
            ["ERR_PROTOCOL_VERSION"] = new GameErrorInfo("ERR_PROTOCOL_VERSION", "协议版本不兼容，请更新客户端", false,
                "客户端协议世代与服务器不一致。请更新客户端后再连。"),
            ["ERR_SCHEMA_MISMATCH"] = new GameErrorInfo("ERR_SCHEMA_MISMATCH", "协议 schema 与服务器不一致", false,
                "game.proto 哈希与服务器不同。请导入服务器当前 schema 后重编客户端。"),
            ["ERR_CLIENT_UPGRADE_REQUIRED"] = new GameErrorInfo("ERR_CLIENT_UPGRADE_REQUIRED", "客户端版本过低，请更新", false,
                "客户端版本低于服务器要求。请更新后再连。"),
            ["ERR_RATE_LIMITED"] = new GameErrorInfo("ERR_RATE_LIMITED", "请求过于频繁", true,
                "触发了连接/帧/心跳/登录限流。请稍等几秒再试，不要连点。"),
            ["ERR_OVERLOADED"] = new GameErrorInfo("ERR_OVERLOADED", "服务器过载，请稍后重试", true,
                "服务器队列过载或正在摘流。请稍后重试。"),
            ["ERR_DEPENDENCY_UNAVAILABLE"] = new GameErrorInfo("ERR_DEPENDENCY_UNAVAILABLE", "依赖服务暂不可用", true,
                "Auth/Session/GameDB 等依赖暂时不可用。请稍后重试；若持续失败请看服务器健康状态。"),
            ["ERR_MAP_FULL"] = new GameErrorInfo("ERR_MAP_FULL", "当前地图实例已满", false,
                "这张地图实例人数已满。请换一个模板或稍后再进，不要改客户端硬选实例。"),
            ["ERR_MAP_DATA_MISMATCH"] = new GameErrorInfo("ERR_MAP_DATA_MISMATCH", "地图数据版本或哈希不匹配，请更新资源", false,
                "本地地图静态数据与服务器不符。请更新地图资源后再进图。"),
            ["ERR_NOT_ON_MAP"] = new GameErrorInfo("ERR_NOT_ON_MAP", "尚未进入地图", false,
                "还没进图。请先登录成功，等自动进图或再点进图。"),
            ["ERR_STALE_SEQ"] = new GameErrorInfo("ERR_STALE_SEQ", "客户端序号过旧", false,
                "这条请求的 seq 比服务器记录的旧。以服务器为准，下一条用新 seq。"),
            ["ERR_MOVE_TOO_FAST"] = new GameErrorInfo("ERR_MOVE_TOO_FAST", "移动过快，已按服务器位置校正", true,
                "客户端移动超过允许速度。角色会被拉回服务器位置，请按校正后的坐标继续。"),
            ["ERR_UNWALKABLE"] = new GameErrorInfo("ERR_UNWALKABLE", "目标位置不可行走", false,
                "目标格子不能走。请换一个可达位置。"),
            ["ERR_OUT_OF_BOUNDS"] = new GameErrorInfo("ERR_OUT_OF_BOUNDS", "目标位置超出地图边界", false,
                "坐标超出地图范围。请回到地图内再移动。"),
            ["ERR_AOI_RESYNC_REQUIRED"] = new GameErrorInfo("ERR_AOI_RESYNC_REQUIRED", "视野需要全量同步", true,
                "AOI 序号有缺口。客户端应请求世界快照后再继续。"),
            ["ERR_SNAPSHOT_TOO_LARGE"] = new GameErrorInfo("ERR_SNAPSHOT_TOO_LARGE", "世界快照过大", false,
                "快照里的 AOI 实体超过上限。请缩小视野或稍后重试。"),
            ["ERR_PLAYER_DEAD"] = new GameErrorInfo("ERR_PLAYER_DEAD", "角色已死亡，请复活", false,
                "HP 为 0，移动等写操作被拒绝。请点复活。"),
            ["ERR_MAIL_RATE_LIMIT"] = new GameErrorInfo("ERR_MAIL_RATE_LIMIT", "邮件发送过于频繁", true,
                "发邮件太快。请稍后再发。"),
            ["ERR_MAIL_RECEIVER_NOT_FOUND"] = new GameErrorInfo("ERR_MAIL_RECEIVER_NOT_FOUND", "收件人不存在", false,
                "收件人玩家 ID 不存在。请核对后再发。"),
            ["ERR_MAIL_SELF"] = new GameErrorInfo("ERR_MAIL_SELF", "不能给自己发邮件", false,
                "发件人和收件人是同一个玩家。请换成其他玩家 ID。"),
            ["ERR_COMMAND_FORBIDDEN"] = new GameErrorInfo("ERR_COMMAND_FORBIDDEN", "该命令不被允许", false,
                "这条公网命令被策略拒绝。不要重试同一条非法命令。"),
            ["ERR_INTERNAL"] = new GameErrorInfo("ERR_INTERNAL", "服务器内部错误", false,
                "服务器内部异常。请带追踪号反馈；不要靠连点重试同一条非幂等写。"),
            ["ERR_BAD_CREDENTIAL"] = new GameErrorInfo("ERR_BAD_CREDENTIAL", "账号或密码错误", false,
                "密码为空、不足 6 位，或与该玩家 ID 注册时不一致。请填回至少 6 位密码（联调可用 demo-local）后点登录；若仍失败，用同一密码先注册再登录。关窗口不会登出。"),
            ["ERR_ACCOUNT_NOT_FOUND"] = new GameErrorInfo("ERR_ACCOUNT_NOT_FOUND", "账号未注册，请先注册", false,
                "这个玩家 ID 还没有账号。请先填至少 6 位密码点注册，成功后再登录。"),
            ["ERR_BANNED"] = new GameErrorInfo("ERR_BANNED", "账号已封禁", false,
                "该账号被封禁，无法登录。"),
            ["INVALID_ARG"] = new GameErrorInfo("ERR_INVALID_ARGUMENT", "请求参数非法", false,
                "常见原因：设备 ID 为空，或密码为空/不足 6 位。注册和登录都必须带同一份密码。"),
            ["BAD_CREDENTIAL"] = new GameErrorInfo("ERR_BAD_CREDENTIAL", "账号或密码错误", false,
                "密码为空、不足 6 位，或与该玩家 ID 注册时不一致。请填回密码后点登录。")
        };

        public static bool TryDescribeAuthInput(string deviceId, string password, bool login,
            ulong playerId, out string code, out string message)
        {
            code = "ERR_INVALID_ARGUMENT";
            message = "";
            if (string.IsNullOrEmpty(deviceId))
            {
                message = "设备ID为空。请在「设备ID」栏填一个稳定标识（不要留空）后再点注册或登录。";
                return true;
            }
            var pw = password ?? "";
            if (pw.Length == 0)
            {
                code = login ? "ERR_BAD_CREDENTIAL" : "ERR_INVALID_ARGUMENT";
                message = login
                    ? "密码为空。登录需要至少 6 位密码。登录成功后密码框会被清空，再次登录请再填一次（联调可用 demo-local）。关窗口不会登出。"
                    : "密码为空。注册需要至少 6 位密码，请填好后再点注册。";
                return true;
            }
            if (pw.Length < 6)
            {
                message = "密码只有 " + pw.Length + " 位，服务器要求至少 6 位。请加长后再点。";
                return true;
            }
            if (login && playerId == 0)
            {
                code = "ERR_ACCOUNT_NOT_FOUND";
                message = "玩家ID为空。请先点注册拿到玩家ID，或把已有玩家ID填进「玩家ID」栏后再登录。";
                return true;
            }
            code = "";
            return false;
        }

        public static GameErrorInfo Resolve(string code, string fallback = "")
        {
            if (!string.IsNullOrEmpty(code) && Map.TryGetValue(code, out var info))
            {
                if (code == GameMeshErrorCode.ServerError && !string.IsNullOrEmpty(fallback) &&
                    fallback != info.Chinese)
                    return new GameErrorInfo(code, info.Chinese, info.Retryable, fallback);
                return info;
            }
            if (!string.IsNullOrEmpty(code) && code.StartsWith("ERR_MAIL_"))
                return new GameErrorInfo(code, "邮件请求失败", false,
                    string.IsNullOrEmpty(fallback) ? "邮件接口返回失败，请核对收件人和操作后重试。" : fallback);
            if (!string.IsNullOrEmpty(code))
            {
                var title = string.IsNullOrEmpty(fallback) ? code : fallback;
                return new GameErrorInfo(code, title, false, ExplainUnknown(code, fallback));
            }
            if (!string.IsNullOrEmpty(fallback))
                return new GameErrorInfo(GameMeshErrorCode.ServerError, "服务器返回错误", true, fallback);
            return new GameErrorInfo(GameMeshErrorCode.ServerError, "未知错误", true,
                "没有错误码也没有原因文本。请重试；若反复出现，看客户端日志里的原始回包。");
        }

        public static string FormatUi(string code, string fallback = "", string traceShort = "")
        {
            var info = Resolve(code, fallback);
            var retry = info.Retryable ? "可重试" : "不可重试";
            var ui = info.Code + "  " + info.Chinese + "  (" + retry + ")";
            var detail = PickDetail(info, fallback);
            if (!string.IsNullOrEmpty(detail) && detail != info.Chinese)
                ui += "\n" + detail;
            if (!string.IsNullOrEmpty(traceShort))
                ui += "\n追踪  #" + traceShort;
            return ui;
        }

        public static bool IsSessionReplaced(string code)
        {
            return code == GameMeshErrorCode.SessionReplaced ||
                   code == "ERR_SESSION_REPLACED" ||
                   code == "ERR_FENCE_STALE";
        }

        static string PickDetail(GameErrorInfo info, string fallback)
        {
            var extra = TranslateDiag(fallback);
            if (!string.IsNullOrEmpty(extra))
                return extra;
            return info.Detail;
        }

        static string ExplainUnknown(string code, string fallback)
        {
            var extra = TranslateDiag(fallback);
            if (!string.IsNullOrEmpty(extra))
                return extra;
            if (!string.IsNullOrEmpty(fallback))
                return fallback;
            return "错误码 " + code + "。请按联调日志核对原因后再试。";
        }

        static string TranslateDiag(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            var s = raw.Trim();
            if (s.IndexOf("password(>=6)", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("device_id and password", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "服务器要求：设备 ID 必填，密码至少 6 位。当前密码为空或太短，所以注册被拒绝。请填好后再点注册。";
            if (s.IndexOf("invalid credential", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("bad credential", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "服务器判定凭证无效。密码为空、不足 6 位，或和这个玩家 ID 注册时不一致，都会这样。请填回至少 6 位密码后点登录；若仍失败，用同一密码先注册再登录。";
            if (s.IndexOf("account not registered", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("account not found", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "这个玩家 ID 还没注册。请先带至少 6 位密码点注册，成功后再登录。";
            if (s.IndexOf("account has no password", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "这个账号还没有密码（非正式模式遗留）。请带至少 6 位密码重新注册后再登录。";
            if (s.IndexOf("missing login response", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "没有收到登录回包。请确认已连上 Gateway 后再点登录。";
            if (s.IndexOf("login body missing", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return "登录成功包缺少 session/token。请重试登录；若反复出现，看服务器日志。";
            if (s.IndexOf("设备ID和密码", System.StringComparison.Ordinal) >= 0)
                return "设备 ID 和密码都要填。密码至少 6 位；空着点按钮会被直接拒绝，不会发到服务器。";
            if (ContainsCjk(s))
                return s;
            return "";
        }

        static bool ContainsCjk(string s)
        {
            foreach (var c in s)
            {
                if (c >= 0x4e00 && c <= 0x9fff)
                    return true;
            }
            return false;
        }
    }
}
