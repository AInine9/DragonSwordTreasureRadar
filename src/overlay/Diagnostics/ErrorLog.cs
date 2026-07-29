using System;
using System.IO;

namespace DragonSwordTreasureRadar
{
    internal static class ErrorLog
    {
        public static readonly string Path = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "DragonSwordTreasureRadar.log");

        public static void Write(
            string context,
            Exception exception)
        {
            string details = exception == null
                ? "(no exception details)"
                : exception.ToString();
            Append(
                Timestamp() + " " + context + Environment.NewLine +
                details + Environment.NewLine + Environment.NewLine);
        }

        public static void WriteDebug(string message)
        {
            if (DebugSettings.Enabled)
            {
                WriteMessage(message);
            }
        }

        public static void WriteMessage(string message)
        {
            Append(
                Timestamp() + " " + message +
                Environment.NewLine);
        }

        private static string Timestamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static void Append(string text)
        {
            try
            {
                File.AppendAllText(Path, text);
            }
            catch
            {
                // Logging must never terminate the overlay.
            }
        }
    }
}
