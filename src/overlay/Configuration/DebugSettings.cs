using System;
using System.IO;

namespace DragonSwordTreasureRadar
{
    internal static class DebugSettings
    {
        private const string SettingName = "debug_logging";
        private static readonly TimeSpan RefreshInterval =
            TimeSpan.FromSeconds(1);

        private static DateTime _nextRefreshUtc;
        private static bool _enabled;

        public static bool Enabled
        {
            get
            {
                Refresh();
                return _enabled;
            }
        }

        private static void Refresh()
        {
            if (DateTime.UtcNow < _nextRefreshUtc)
            {
                return;
            }

            _nextRefreshUtc = DateTime.UtcNow.Add(RefreshInterval);
            _enabled = false;

            try
            {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "scripts",
                    "config.lua");
                if (!File.Exists(path))
                {
                    return;
                }

                foreach (string sourceLine in File.ReadLines(path))
                {
                    string line = RemoveComment(sourceLine).Trim();
                    if (!line.StartsWith(
                        SettingName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int equals = line.IndexOf('=');
                    if (equals < 0)
                    {
                        continue;
                    }

                    string value = line.Substring(equals + 1)
                        .Trim()
                        .TrimEnd(',');
                    _enabled = string.Equals(
                        value,
                        "true",
                        StringComparison.OrdinalIgnoreCase);
                    return;
                }
            }
            catch (IOException)
            {
                _enabled = false;
            }
            catch (UnauthorizedAccessException)
            {
                _enabled = false;
            }
        }

        private static string RemoveComment(string line)
        {
            int comment = line.IndexOf(
                "--",
                StringComparison.Ordinal);
            return comment < 0
                ? line
                : line.Substring(0, comment);
        }
    }
}
