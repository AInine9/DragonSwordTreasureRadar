namespace DragonSwordTreasureRadar
{
    internal sealed class WorldTreasure
    {
        public long SaveId { get; set; }
        public int MapId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public bool HasZ { get; set; }
        public string UidName { get; set; }
        public long GroupId { get; set; }

        public TreasureKind Kind
        {
            get { return TreasureIdentity.GetKind(UidName); }
        }

        public string DebugName
        {
            get
            {
                return TreasureIdentity.GetDebugName(
                    UidName,
                    SaveId);
            }
        }
    }
}
