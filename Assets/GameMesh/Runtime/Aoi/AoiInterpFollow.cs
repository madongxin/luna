namespace GameMesh.Aoi
{
    /// <summary>
    /// Present-layer step from the current mesh to the delayed sample. Walks gaps
    /// greater than 0.4m at move_speed. Within 8m catch-up is 1.5x. Never snaps
    /// unless allowSnap is on. Does not read vx, vz, or move_start_ms.
    /// </summary>
    public static class AoiInterpFollow
    {
        public const float WalkThreshold = 0.4f;
        public const float CatchupMeters = 8f;
        public const float CatchupMul = 1.5f;
        public const float ReverseBrakeMeters = 2f;

        public struct StepResult
        {
            public float X, Z;
            public float Speed;
            public bool Catchup;
            public bool TeleportLogged;
            public bool Brake;
            public bool Snapped;
        }

        public static StepResult Step(float px, float pz, float qx, float qz,
            float lastSegX, float lastSegZ, float dt, float moveSpeed, bool allowSnap)
        {
            if (dt < 0.001f)
                dt = 0.001f;
            if (dt > 0.05f)
                dt = 0.05f;
            if (moveSpeed < 0.01f)
                moveSpeed = 10f;

            var dx = qx - px;
            var dz = qz - pz;
            var dist = (float)System.Math.Sqrt(dx * dx + dz * dz);
            var result = new StepResult { X = px, Z = pz };
            if (dist <= 0.02f)
            {
                result.X = qx;
                result.Z = qz;
                return result;
            }

            if (dist <= WalkThreshold)
            {
                result.X = qx;
                result.Z = qz;
                result.Speed = dist / dt;
                return result;
            }

            if (allowSnap && dist > CatchupMeters)
            {
                result.X = qx;
                result.Z = qz;
                result.Snapped = true;
                result.TeleportLogged = true;
                result.Speed = dist / dt;
                return result;
            }

            var speed = moveSpeed;
            if (dist <= CatchupMeters)
            {
                speed = moveSpeed * CatchupMul;
                result.Catchup = true;
            }
            else
                result.TeleportLogged = true;

            var incoming = dx * lastSegX + dz * lastSegZ;
            var seg2 = lastSegX * lastSegX + lastSegZ * lastSegZ;
            if (seg2 > 0.0001f && incoming < 0f && dist >= ReverseBrakeMeters)
            {
                result.Brake = true;
                result.Speed = 0f;
                return result;
            }

            var maxStep = speed * dt;
            if (maxStep >= dist)
            {
                result.X = qx;
                result.Z = qz;
                result.Speed = dist / dt;
                return result;
            }

            var u = maxStep / dist;
            result.X = px + dx * u;
            result.Z = pz + dz * u;
            result.Speed = speed;
            return result;
        }
    }
}
