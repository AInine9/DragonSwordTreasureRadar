using System.Collections.Generic;

namespace DragonSwordTreasureRadar
{
    internal sealed class RadarState
    {
        public bool enabled { get; set; }
        public double radius { get; set; }
        public List<RadarPoint> points { get; set; }
    }

    internal sealed class RadarPoint
    {
        public long saveId { get; set; }
        public double dx { get; set; }
        public double dy { get; set; }
    }
}
