using System.Collections.Generic;
using GameMesh.Network;

namespace GameMesh.Protocol
{
    public readonly struct GameErrorInfo
    {
        public readonly string Code;
        public readonly string Chinese;
        public readonly bool Retryable;

        public GameErrorInfo(string code, string chinese, bool retryable)
        {
            Code = code ?? "";
            Chinese = chinese ?? "";
            Retryable = retryable;
        }
    }

    public static class GameErrorCatalog
    {
        static readonly Dictionary<string, GameErrorInfo> Map = new Dictionary<string, GameErrorInfo>
        {
            [GameMeshErrorCode.ClientNotConnected] = new GameErrorInfo(GameMeshErrorCode.ClientNotConnected, "尚未连接到服务器", true),
            [GameMeshErrorCode.ClientIllegalState] = new GameErrorInfo(GameMeshErrorCode.ClientIllegalState, "当前状态不允许该操作", false),
            [GameMeshErrorCode.ClientTimeout] = new GameErrorInfo(GameMeshErrorCode.ClientTimeout, "请求超时", true),
            [GameMeshErrorCode.ClientCancelled] = new GameErrorInfo(GameMeshErrorCode.ClientCancelled, "请求已取消", true),
            [GameMeshErrorCode.ClientProtocol] = new GameErrorInfo(GameMeshErrorCode.ClientProtocol, "协议解析失败", false),
            [GameMeshErrorCode.ClientQueueFull] = new GameErrorInfo(GameMeshErrorCode.ClientQueueFull, "请求过多，请稍后重试", true),
            [GameMeshErrorCode.ClientDisconnected] = new GameErrorInfo(GameMeshErrorCode.ClientDisconnected, "连接已断开", true),
            [GameMeshErrorCode.ClientInvalidCoord] = new GameErrorInfo(GameMeshErrorCode.ClientInvalidCoord, "坐标非法", false),
            [GameMeshErrorCode.MapHashMismatch] = new GameErrorInfo(GameMeshErrorCode.MapHashMismatch, "本地地图与服务器不一致", false),
            [GameMeshErrorCode.ProtocolMissing] = new GameErrorInfo(GameMeshErrorCode.ProtocolMissing, "当前协议缺少所需类型", false),
            [GameMeshErrorCode.ServerError] = new GameErrorInfo(GameMeshErrorCode.ServerError, "服务器返回错误", true),
            ["ERR_INVALID_ARGUMENT"] = new GameErrorInfo("ERR_INVALID_ARGUMENT", "请求参数非法", false),
            ["ERR_UNAUTHENTICATED"] = new GameErrorInfo("ERR_UNAUTHENTICATED", "未完成握手或未登录", false),
            ["ERR_FENCE_STALE"] = new GameErrorInfo("ERR_FENCE_STALE", "会话栅栏已过期", false),
            ["ERR_SESSION_EXPIRED"] = new GameErrorInfo("ERR_SESSION_EXPIRED", "会话已过期，请重新登录", false),
            [GameMeshErrorCode.SessionReplaced] = new GameErrorInfo(GameMeshErrorCode.SessionReplaced, "账号已在其他设备登录", false),
            ["ERR_PROTOCOL_VERSION"] = new GameErrorInfo("ERR_PROTOCOL_VERSION", "协议版本不兼容，请更新客户端", false),
            ["ERR_SCHEMA_MISMATCH"] = new GameErrorInfo("ERR_SCHEMA_MISMATCH", "协议 schema 与服务器不一致", false),
            ["ERR_CLIENT_UPGRADE_REQUIRED"] = new GameErrorInfo("ERR_CLIENT_UPGRADE_REQUIRED", "客户端版本过低，请更新", false),
            ["ERR_RATE_LIMITED"] = new GameErrorInfo("ERR_RATE_LIMITED", "请求过于频繁", true),
            ["ERR_OVERLOADED"] = new GameErrorInfo("ERR_OVERLOADED", "服务器过载，请稍后重试", true),
            ["ERR_DEPENDENCY_UNAVAILABLE"] = new GameErrorInfo("ERR_DEPENDENCY_UNAVAILABLE", "依赖服务暂不可用", true),
            ["ERR_MAP_FULL"] = new GameErrorInfo("ERR_MAP_FULL", "当前地图实例已满", false),
            ["ERR_MAP_DATA_MISMATCH"] = new GameErrorInfo("ERR_MAP_DATA_MISMATCH", "地图数据版本或哈希不匹配，请更新资源", false),
            ["ERR_NOT_ON_MAP"] = new GameErrorInfo("ERR_NOT_ON_MAP", "尚未进入地图", false),
            ["ERR_STALE_SEQ"] = new GameErrorInfo("ERR_STALE_SEQ", "客户端序号过旧", false),
            ["ERR_MOVE_TOO_FAST"] = new GameErrorInfo("ERR_MOVE_TOO_FAST", "移动过快，已按服务器位置校正", true),
            ["ERR_UNWALKABLE"] = new GameErrorInfo("ERR_UNWALKABLE", "目标位置不可行走", false),
            ["ERR_OUT_OF_BOUNDS"] = new GameErrorInfo("ERR_OUT_OF_BOUNDS", "目标位置超出地图边界", false),
            ["ERR_AOI_RESYNC_REQUIRED"] = new GameErrorInfo("ERR_AOI_RESYNC_REQUIRED", "视野需要全量同步", true),
            ["ERR_SNAPSHOT_TOO_LARGE"] = new GameErrorInfo("ERR_SNAPSHOT_TOO_LARGE", "世界快照过大", false),
            ["ERR_PLAYER_DEAD"] = new GameErrorInfo("ERR_PLAYER_DEAD", "角色已死亡，请复活", false),
            ["ERR_MAIL_RATE_LIMIT"] = new GameErrorInfo("ERR_MAIL_RATE_LIMIT", "邮件发送过于频繁", true),
            ["ERR_MAIL_RECEIVER_NOT_FOUND"] = new GameErrorInfo("ERR_MAIL_RECEIVER_NOT_FOUND", "收件人不存在", false),
            ["ERR_MAIL_SELF"] = new GameErrorInfo("ERR_MAIL_SELF", "不能给自己发邮件", false),
            ["ERR_COMMAND_FORBIDDEN"] = new GameErrorInfo("ERR_COMMAND_FORBIDDEN", "该命令不被允许", false),
            ["ERR_INTERNAL"] = new GameErrorInfo("ERR_INTERNAL", "服务器内部错误", false)
        };

        public static GameErrorInfo Resolve(string code, string fallback = "")
        {
            if (!string.IsNullOrEmpty(code) && Map.TryGetValue(code, out var info))
                return info;
            if (!string.IsNullOrEmpty(code) && code.StartsWith("ERR_MAIL_"))
                return new GameErrorInfo(code, string.IsNullOrEmpty(fallback) ? "邮件请求失败" : fallback, false);
            if (!string.IsNullOrEmpty(code))
                return new GameErrorInfo(code, string.IsNullOrEmpty(fallback) ? code : fallback, false);
            return new GameErrorInfo(GameMeshErrorCode.ServerError,
                string.IsNullOrEmpty(fallback) ? "未知错误" : fallback, true);
        }

        public static string FormatUi(string code, string fallback = "", string traceShort = "")
        {
            var info = Resolve(code, fallback);
            var retry = info.Retryable ? "可重试" : "不可重试";
            var ui = $"{info.Code}  {info.Chinese}  ({retry})";
            if (!string.IsNullOrEmpty(traceShort))
                ui += "  #" + traceShort;
            return ui;
        }

        public static bool IsSessionReplaced(string code)
        {
            return code == GameMeshErrorCode.SessionReplaced ||
                   code == "ERR_SESSION_REPLACED" ||
                   code == "ERR_FENCE_STALE";
        }
    }
}
