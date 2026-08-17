using GameMesh.Network;
using GameMesh.Protocol;

namespace GameMesh.Auth
{
    public static class AuthResponse
    {
        public static bool TryAcceptLogin(GameResponse rsp, ulong fallbackPlayerId,
            out ulong playerId, out LoginRsp login, out string errorCode, out string message)
        {
            login = rsp != null ? rsp.Login : null;
            playerId = 0;
            errorCode = "";
            message = "";
            if (rsp == null)
            {
                errorCode = GameMeshErrorCode.ServerError;
                message = "missing login response";
                return false;
            }

            if (!rsp.Ok || login == null || !login.Ok)
            {
                errorCode = ProtocolMapper.ExtractErrorCode(rsp);
                if (string.IsNullOrEmpty(errorCode))
                    errorCode = GameMeshErrorCode.ServerError;
                message = login != null && !string.IsNullOrEmpty(login.Message) ? login.Message : rsp.Message;
                return false;
            }

            playerId = login.Profile != null && login.Profile.PlayerId != 0
                ? login.Profile.PlayerId
                : fallbackPlayerId;
            if (playerId == 0 || string.IsNullOrEmpty(login.SessionId) ||
                string.IsNullOrEmpty(login.Token) || login.Generation == 0)
            {
                errorCode = GameMeshErrorCode.ServerError;
                message = "login body missing session identity";
                return false;
            }

            return true;
        }

        public static LogoutResult FromLogout(GameResponse rsp, bool requestSent)
        {
            var result = new LogoutResult { RequestSent = requestSent };
            if (!requestSent)
            {
                result.ErrorCode = GameMeshErrorCode.ClientIllegalState;
                result.Message = "logout request was not sent";
                return result;
            }

            if (rsp == null)
            {
                result.ErrorCode = GameMeshErrorCode.ServerError;
                result.Message = "logout response missing";
                return result;
            }

            result.TopLevelOk = rsp.Ok;
            result.BodyOk = rsp.Logout != null && rsp.Logout.Ok;
            if (!result.AuthorityOk)
            {
                result.ErrorCode = ProtocolMapper.ExtractErrorCode(rsp);
                if (string.IsNullOrEmpty(result.ErrorCode))
                    result.ErrorCode = GameMeshErrorCode.ServerError;
                result.Message = rsp.Logout != null && !string.IsNullOrEmpty(rsp.Logout.Message)
                    ? rsp.Logout.Message
                    : rsp.Message ?? "";
            }

            return result;
        }
    }
}
