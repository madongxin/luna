namespace GameMesh.Player
{
    public sealed class PlayerAttributeSnapshot
    {
        public ulong PlayerId;
        public string Name = "";
        public float Hp = 100f;
        public float MaxHp = 100f;
        public float Mp;
        public float MaxMp;
        public float Attack;
        public float SpellPower;
        public float Defense;
        public float MagicResist;
        public float CritRate;
        public float CritDamage;
        public float MoveSpeed = 10f;
        public float AttackSpeed = 1f;
        public bool FromServer;
    }
}
