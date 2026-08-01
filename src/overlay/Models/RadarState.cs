using System.Collections.Generic;

namespace DragonSwordTreasureRadar
{
    internal sealed class RadarState
    {
        public bool enabled { get; set; }
        public string mode { get; set; }
        public double radius { get; set; }
        public List<RadarPoint> points { get; set; }
        public WorldMapState worldMap { get; set; }
    }

    internal sealed class RadarPoint
    {
        public long saveId { get; set; }
        public double dx { get; set; }
        public double dy { get; set; }
    }

    internal sealed class WorldMapState
    {
        public int mapId { get; set; }
        public double dimensions { get; set; }
        public double uiSize { get; set; }
        public double left { get; set; }
        public double top { get; set; }
        public double zoom { get; set; }
        public double viewportWidth { get; set; }
        public double viewportHeight { get; set; }
        public double viewportScale { get; set; }
        public double playerWorldX { get; set; }
        public double playerWorldY { get; set; }
        public double playerMapX { get; set; }
        public double playerMapY { get; set; }
    }
}
