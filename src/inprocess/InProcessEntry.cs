using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace DragonSwordTreasureRadar
{
    public static class InProcessEntry
    {
        private static int _started;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        public static int Start(string root)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return 0;
            RuntimePaths.BaseDirectory = Path.GetFullPath(root);
            var worker = new Thread(Run) { IsBackground = true, Name = "DragonSword Radar Data" };
            worker.Start();
            return 0;
        }

        private static void Run()
        {
            try
            {
                string root = RuntimePaths.BaseDirectory;
                if (LoadLibrary(Path.Combine(root, "e_sqlcipher.dll")) == IntPtr.Zero)
                    throw new InvalidOperationException("SQLCipher DLL load failed: " + Marshal.GetLastWin32Error());
                string cache = Path.Combine(root, "cache");
                Directory.CreateDirectory(cache);
                string data = Path.Combine(cache, "treasures.lua");
                string statePath = Path.Combine(cache, "opened.lua");
                // Invalidate old process state immediately, before any game or database reads.
                AtomicWrite(statePath, "return {ready=false,opened={}}\n");
                using (Process game = GameProcessFinder.FindNewest())
                {
                    if (game == null) throw new InvalidOperationException("Game process not found.");
                    string exe = game.MainModule.FileName;
                    string pak = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe), "..", "..", "Content", "Paks", "pakchunk109-WindowsClient.pak"));
                    string stamp = new FileInfo(exe).Length + ":" + File.GetLastWriteTimeUtc(exe).Ticks + ":" + new FileInfo(pak).Length + ":" + File.GetLastWriteTimeUtc(pak).Ticks;
                    string stampPath = Path.Combine(cache, "catalog.version");
                    if (!File.Exists(data) || !File.Exists(stampPath) || File.ReadAllText(stampPath) != stamp)
                    {
                        string next = data + ".next";
                        int count = global::Program.GenerateInProcess(exe, pak, next);
                        Replace(next, data);
                        AtomicWrite(stampPath, stamp);
                        ErrorLog.WriteMessage("In-process catalog generated: " + count + " locations.");
                    }
                }
                long[] ids = Regex.Matches(File.ReadAllText(data), @"save_id\s*=\s*(\d+)")
                    .Cast<Match>().Select(m => long.Parse(m.Groups[1].Value)).Distinct().ToArray();
                var save = new TreasureSaveState();
                int lastVersion = -1;
                bool wasReady = false;
                while (true)
                {
                    save.Refresh();
                    bool ready = save.HasLoadedSaveState && save.LastErrorSummary == "none";
                    if (save.Version != lastVersion || ready != wasReady)
                    {
                        var output = new StringBuilder("return {ready=").Append(ready ? "true" : "false")
                            .Append(",opened={");
                        int hidden = 0;
                        if (ready) foreach (long id in ids)
                        {
                            if (save.IsOpened(id)) { output.Append('[').Append(id).Append("]=true,"); hidden++; }
                        }
                        output.Append("}}\n");
                        AtomicWrite(statePath, output.ToString());
                        lastVersion = save.Version;
                        wasReady = ready;
                        ErrorLog.WriteMessage("In-process save state: ready=" + ready + " openedBits=" + save.OpenedBitCount + " hiddenMarkers=" + hidden);
                    }
                    Thread.Sleep(500);
                }
            }
            catch (Exception exception) { ErrorLog.Write("In-process data worker stopped", exception); }
        }

        private static void AtomicWrite(string path, string contents)
        {
            string next = path + ".next";
            File.WriteAllText(next, contents, new UTF8Encoding(false));
            Replace(next, path);
        }

        private static void Replace(string source, string target)
        {
            if (File.Exists(target)) File.Replace(source, target, null);
            else File.Move(source, target);
        }
    }
}
