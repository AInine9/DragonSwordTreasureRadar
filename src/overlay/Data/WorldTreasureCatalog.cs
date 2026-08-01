using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace DragonSwordTreasureRadar
{
    internal sealed class WorldTreasureCatalog
    {
        private static readonly Regex TreasurePattern = new Regex(
            @"save_id\s*=\s*(\d+),\s*section\s*=\s*""(\d+)"",\s*" +
            @"x\s*=\s*([-+0-9.eE]+),\s*y\s*=\s*([-+0-9.eE]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly string _path;
        private DateTime _lastWriteUtc;
        private DateTime _nextRefreshUtc;
        private bool _hasLoaded;
        private int _version;
        private List<WorldTreasure> _points =
            new List<WorldTreasure>();

        public WorldTreasureCatalog()
        {
            _path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "scripts",
                "treasures.lua");
        }

        public IList<WorldTreasure> Points
        {
            get { return _points; }
        }

        public int Version
        {
            get { return _version; }
        }

        public void Refresh()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextRefreshUtc)
            {
                return;
            }
            _nextRefreshUtc = now.AddSeconds(5);

            if (!File.Exists(_path))
            {
                return;
            }
            DateTime writeTime = File.GetLastWriteTimeUtc(_path);
            if (_hasLoaded && writeTime == _lastWriteUtc)
            {
                return;
            }

            List<WorldTreasure> loaded =
                new List<WorldTreasure>();
            foreach (string line in File.ReadLines(_path))
            {
                Match match = TreasurePattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                string section = match.Groups[2].Value;
                int mapId;
                long saveId;
                double x;
                double y;
                if (section.Length < 3
                    || !int.TryParse(
                        section.Substring(section.Length - 3),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out mapId)
                    || !long.TryParse(
                        match.Groups[1].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out saveId)
                    || !double.TryParse(
                        match.Groups[3].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out x)
                    || !double.TryParse(
                        match.Groups[4].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out y))
                {
                    continue;
                }
                loaded.Add(new WorldTreasure
                {
                    SaveId = saveId,
                    MapId = mapId,
                    X = x,
                    Y = y
                });
            }

            _points = loaded;
            _lastWriteUtc = writeTime;
            _hasLoaded = true;
            _version++;
        }
    }
}
