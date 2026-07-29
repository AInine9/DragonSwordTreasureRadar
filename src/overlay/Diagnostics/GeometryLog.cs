using System;
using System.IO;

namespace DragonSwordTreasureRadar
{
    internal static class GeometryLog
    {
        private static readonly string Path = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "DragonSwordTreasureRadarGeometry.log");

        public static void Write(string message)
        {
            if (!DebugSettings.Enabled)
            {
                return;
            }

            try
            {
                File.AppendAllText(
                    Path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " " + message + Environment.NewLine);
            }
            catch
            {
                // Diagnostic logging must never terminate the overlay.
            }
        }
    }
}
