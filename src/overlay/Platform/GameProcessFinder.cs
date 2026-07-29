using System;
using System.Diagnostics;

namespace DragonSwordTreasureRadar
{
    internal static class GameProcessFinder
    {
        private const string ProcessName =
            "DSClient-Win64-Shipping";

        public static Process FindNewest()
        {
            Process selected = null;
            DateTime selectedStartTime = DateTime.MinValue;

            foreach (Process candidate in
                Process.GetProcessesByName(ProcessName))
            {
                try
                {
                    DateTime startTime = candidate.StartTime;
                    if (selected == null
                        || startTime > selectedStartTime)
                    {
                        if (selected != null)
                        {
                            selected.Dispose();
                        }
                        selected = candidate;
                        selectedStartTime = startTime;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }
                catch
                {
                    candidate.Dispose();
                }
            }

            return selected;
        }
    }
}
