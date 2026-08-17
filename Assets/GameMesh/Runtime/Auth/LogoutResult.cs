namespace GameMesh.Auth
{
    public sealed class LogoutResult
    {
        public bool RequestSent;
        public bool TopLevelOk;
        public bool BodyOk;
        public bool TransportDisconnected;
        public string ErrorCode = "";
        public string Message = "";

        public bool AuthorityOk => RequestSent && TopLevelOk && BodyOk;
    }
}
