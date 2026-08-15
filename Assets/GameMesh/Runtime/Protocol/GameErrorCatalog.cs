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
            [GameMeshErrorCode.HelloBlocked] = new GameErrorInfo(GameMeshErrorCode.HelloBlocked, "服务器尚未提供 Hello/能力协商", false),
            [GameMeshErrorCode.SnapshotBlocked] = new GameErrorInfo(GameMeshErrorCode.SnapshotBlocked, "服务器尚未提供世界快照接口", true),
            ["ERR_MAP_DATA_MISMATCH"] = new GameErrorInfo("ERR_MAP_DATA_MISMATCH", "地图数据版本或哈希不匹配", false),
            ["ERR_MOVE_TOO_FAST"] = new GameErrorInfo("ERR_MOVE_TOO_FAST", "移动过快，已按服务器位置校正", true),
            ["ERR_UNWALKABLE"] = new GameErrorInfo("ERR_UNWALKABLE", "目标位置不可行走", false),
            ["ERR_OUT_OF_BOUNDS"] = new GameErrorInfo("ERR_OUT_OF_BOUNDS", "目标位置超出地图边界", false),
            ["ERR_MAIL_RATE_LIMIT"] = new GameErrorInfo("ERR_MAIL_RATE_LIMIT", "邮件发送过于频繁", true),
            ["ERR_MAIL_RECEIVER_NOT_FOUND"] = new GameErrorInfo("ERR_MAIL_RECEIVER_NOT_FOUND", "收件人不存在", false),
            ["ERR_MAIL_SELF"] = new GameErrorInfo("ERR_MAIL_SELF", "不能给自己发邮件", false),
            ["ERR_SESSION_EXPIRED"] = new GameErrorInfo("ERR_SESSION_EXPIRED", "会话已过期，请重新登录", false)
        };

        public static GameErrorInfo Resolve(string code, string fallback = "")
        {
            if (!string.IsNullOrEmpty(code) && Map.TryGetValue(code, out var info))
                return info;
            if (!string.IsNullOrEmpty(code))
                return new GameErrorInfo(code, string.IsNullOrEmpty(fallback) ? code : fallback, true);
            return new GameErrorInfo(GameMeshErrorCode.ServerError,
                string.IsNullOrEmpty(fallback) ? "未知错误" : fallback, true);
        }

        public static string FormatUi(string code, string fallback = "")
        {
            var info = Resolve(code, fallback);
            var retry = info.Retryable ? "可重试" : "不可重试";
            return $"{info.Code}  {info.Chinese}  ({retry})";
        }
    }
}
