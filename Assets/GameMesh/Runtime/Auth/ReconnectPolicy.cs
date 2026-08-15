namespace GameMesh.Auth
{
    public sealed class ReconnectPolicy
    {
        public bool InFlight { get; private set; }
        public float WindowStart { get; private set; }
        public int Attempts { get; private set; }

        public bool TryBegin(float now, int maxAttempts, int maxTotalMs, out string failReason)
        {
            failReason = "";
            if (InFlight)
            {
                failReason = "in-flight";
                return false;
            }

            if (WindowStart <= 0f)
                WindowStart = now;
            if (maxTotalMs > 0 && (now - WindowStart) * 1000f > maxTotalMs)
            {
                failReason = "reconnect budget exceeded";
                return false;
            }

            if (maxAttempts > 0 && Attempts >= maxAttempts)
            {
                failReason = "reconnect attempts exceeded";
                return false;
            }

            InFlight = true;
            Attempts++;
            return true;
        }

        public void EndSuccess()
        {
            InFlight = false;
            Attempts = 0;
            WindowStart = 0f;
        }

        public void EndFailure()
        {
            InFlight = false;
        }

        public void Reset()
        {
            InFlight = false;
            Attempts = 0;
            WindowStart = 0f;
        }
    }
}
