using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DragonSwordTreasureRadar
{
    internal sealed class TreasureSaveState
    {
        private const ulong SaveDatabaseOwnerPointerRva =
            0x94DDB20;
        private const uint ProcessReadAccess =
            0x0010 | 0x1000;
        private const int SqliteOpenReadOnly = 0x00000001;

        private static readonly TimeSpan RefreshInterval =
            TimeSpan.FromMilliseconds(250);

        private readonly Dictionary<int, ulong> _opened =
            new Dictionary<int, ulong>();

        private DateTime _nextRefreshUtc;
        private string _lastDatabasePath;
        private DateTime _lastDatabaseWriteUtc;
        private string _lastKey;
        private string _lastError;
        private string _lastDatabaseAttemptLog;
        private string _lastDatabaseSuccessLog;
        private int _gameProcessId;
        private bool _hasLoadedSaveState;

        public int GameProcessId
        {
            get { return _gameProcessId; }
        }

        public bool HasLoadedSaveState
        {
            get { return _hasLoadedSaveState; }
        }

        public string DatabaseName
        {
            get
            {
                return _lastDatabasePath == null
                    ? "none"
                    : SafeSlotName(_lastDatabasePath);
            }
        }

        public int OpenedBitCount
        {
            get { return CountOpenedBits(_opened); }
        }

        public string LastErrorSummary
        {
            get { return _lastError ?? "none"; }
        }

        public bool IsOpened(long saveId)
        {
            if (saveId <= 0)
            {
                return false;
            }

            int category = (int)(saveId / 64);
            int bit = (int)(saveId % 64);
            ulong field;
            return _opened.TryGetValue(category, out field)
                && (field & (1UL << bit)) != 0;
        }

        public void Refresh()
        {
            if (DateTime.UtcNow < _nextRefreshUtc)
            {
                return;
            }
            _nextRefreshUtc =
                DateTime.UtcNow.Add(RefreshInterval);

            try
            {
                using (Process game =
                    GameProcessFinder.FindNewest())
                {
                    if (game == null)
                    {
                        ResetForGameProcess(0);
                        return;
                    }

                    if (game.Id != _gameProcessId)
                    {
                        ResetForGameProcess(game.Id);
                    }

                    RefreshFromGame(game);
                }
            }
            catch (Exception exception)
            {
                LogRefreshError(exception);
            }
        }

        private void RefreshFromGame(Process game)
        {
            string key = ReadDatabaseKey(game);
            string candidateSummary;
            string databasePath = FindNewestSaveDatabase(
                game,
                out candidateSummary);
            LogDatabaseSelection(
                databasePath,
                candidateSummary);

            DateTime writeTime =
                File.GetLastWriteTimeUtc(databasePath);
            if (databasePath == _lastDatabasePath
                && writeTime == _lastDatabaseWriteUtc
                && key == _lastKey)
            {
                return;
            }

            Dictionary<int, ulong> opened =
                ReadOpenedTreasureBits(databasePath, key);
            ReplaceOpenedState(opened);
            _lastDatabasePath = databasePath;
            _lastDatabaseWriteUtc = writeTime;
            _lastKey = key;
            _lastError = null;
            _hasLoadedSaveState = true;
            LogDatabaseLoaded(databasePath, opened);
        }

        private void ReplaceOpenedState(
            Dictionary<int, ulong> opened)
        {
            _opened.Clear();
            foreach (KeyValuePair<int, ulong> pair in opened)
            {
                _opened[pair.Key] = pair.Value;
            }
        }

        private void LogDatabaseSelection(
            string databasePath,
            string candidateSummary)
        {
            if (!DebugSettings.Enabled)
            {
                return;
            }

            string message =
                "Save-state database selection: selected=" +
                SafeSlotName(databasePath) +
                "; candidates=" + candidateSummary;
            if (message != _lastDatabaseAttemptLog)
            {
                _lastDatabaseAttemptLog = message;
                ErrorLog.WriteDebug(message);
            }
        }

        private void LogDatabaseLoaded(
            string databasePath,
            Dictionary<int, ulong> opened)
        {
            if (!DebugSettings.Enabled)
            {
                return;
            }

            string message =
                "Save-state loaded: database=" +
                SafeSlotName(databasePath) +
                "; categories=" + opened.Count +
                "; openedBits=" + CountOpenedBits(opened);
            if (message != _lastDatabaseSuccessLog)
            {
                _lastDatabaseSuccessLog = message;
                ErrorLog.WriteDebug(message);
            }
        }

        private void LogRefreshError(Exception exception)
        {
            string message =
                exception.GetType().FullName + ": " +
                exception.Message;
            if (message != _lastError)
            {
                _lastError = message;
                ErrorLog.Write(
                    "Save-state refresh failed",
                    exception);
            }
        }

        private void ResetForGameProcess(int processId)
        {
            if (_gameProcessId == processId)
            {
                return;
            }

            _gameProcessId = processId;
            _opened.Clear();
            _lastDatabasePath = null;
            _lastDatabaseWriteUtc = DateTime.MinValue;
            _lastKey = null;
            _lastError = null;
            _lastDatabaseAttemptLog = null;
            _lastDatabaseSuccessLog = null;
            _hasLoadedSaveState = false;
        }

        private static string ReadDatabaseKey(Process game)
        {
            IntPtr process = NativeMethods.OpenProcess(
                ProcessReadAccess,
                false,
                game.Id);
            if (process == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "OpenProcess failed: " +
                    Marshal.GetLastWin32Error());
            }

            try
            {
                ulong moduleBase = unchecked(
                    (ulong)game.MainModule.BaseAddress.ToInt64());
                ulong owner = ReadUInt64(
                    process,
                    moduleBase +
                        SaveDatabaseOwnerPointerRva);
                if (owner == 0)
                {
                    throw new InvalidOperationException(
                        "Save database owner is not ready.");
                }

                ulong keyPointer =
                    ReadUInt64(process, owner + 0x120);
                int keyLength =
                    ReadInt32(process, owner + 0x128);
                if (keyPointer == 0
                    || keyLength <= 1
                    || keyLength > 256)
                {
                    throw new InvalidOperationException(
                        "Save database key is not ready.");
                }

                byte[] bytes = ReadBytes(
                    process,
                    keyPointer,
                    keyLength * 2);
                string key = Encoding.Unicode
                    .GetString(bytes)
                    .TrimEnd('\0');
                if (key.Length == 0
                    || key.Any(character =>
                        character < 0x20
                        || character > 0x7E))
                {
                    throw new InvalidOperationException(
                        "Save database key is not ready.");
                }
                return key;
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        private static string FindNewestSaveDatabase(
            Process game,
            out string candidateSummary)
        {
            string win64 =
                Path.GetDirectoryName(game.MainModule.FileName);
            string saveRoot = Path.GetFullPath(Path.Combine(
                win64,
                "..",
                "..",
                "Saved",
                "SaveGames"));
            List<string> ordered = Directory.GetFiles(
                    saveRoot,
                    "*_Slot*.bak",
                    SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(
                    saveRoot,
                    "*_Slot*.db",
                    SearchOption.AllDirectories))
                .OrderByDescending(
                    File.GetLastWriteTimeUtc)
                .ToList();

            candidateSummary = BuildCandidateSummary(
                ordered,
                win64,
                saveRoot);
            if (ordered.Count == 0)
            {
                throw new FileNotFoundException(
                    "No slot database was found.",
                    saveRoot);
            }
            return ordered[0];
        }

        private static string BuildCandidateSummary(
            IEnumerable<string> candidates,
            string win64,
            string saveRoot)
        {
            string databases = string.Join(
                ",",
                candidates.Select(path =>
                    SafeSlotName(path) + "@" +
                    File.GetLastWriteTimeUtc(path).ToString(
                        "O",
                        CultureInfo.InvariantCulture))
                .ToArray());
            return databases + "; " +
                BuildSlotHints(win64, saveRoot);
        }

        private static string BuildSlotHints(
            string win64,
            string saveRoot)
        {
            string spackSummary = string.Join(
                ",",
                Directory.GetFiles(
                    saveRoot,
                    "SPack_Slot*.sav",
                    SearchOption.AllDirectories)
                .OrderByDescending(
                    File.GetLastWriteTimeUtc)
                .Select(path =>
                    SafeSlotName(path) + "@" +
                    File.GetLastWriteTimeUtc(path).ToString(
                        "O",
                        CultureInfo.InvariantCulture))
                .ToArray());

            string configPath = Path.GetFullPath(Path.Combine(
                win64,
                "..",
                "..",
                "Saved",
                "Config",
                "Windows",
                "Game.ini"));
            string configSummary =
                ReadConfigSlotSummary(configPath);

            return "spack=" +
                (spackSummary.Length == 0
                    ? "none"
                    : spackSummary) +
                "; configSections=" + configSummary;
        }

        private static string ReadConfigSlotSummary(
            string configPath)
        {
            if (!File.Exists(configPath))
            {
                return "none";
            }

            string[] slots = File.ReadLines(configPath)
                .Select(SafeSlotToken)
                .Where(slot => slot != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return slots.Length == 0
                ? "none"
                : string.Join(",", slots);
        }

        private static string SafeSlotName(string path)
        {
            string filename = Path.GetFileName(path);
            string token = SafeSlotToken(filename);
            return token == null
                ? filename
                : token + Path.GetExtension(filename);
        }

        private static string SafeSlotToken(string value)
        {
            int slot = value.IndexOf(
                "_Slot",
                StringComparison.OrdinalIgnoreCase);
            if (slot < 0)
            {
                return null;
            }

            int numberStart = slot + 5;
            int end = numberStart;
            if (end < value.Length && value[end] == '-')
            {
                end++;
            }
            while (end < value.Length
                && char.IsDigit(value[end]))
            {
                end++;
            }

            return end > numberStart
                ? "Slot" + value.Substring(
                    numberStart,
                    end - numberStart)
                : null;
        }

        private static int CountOpenedBits(
            IDictionary<int, ulong> opened)
        {
            int count = 0;
            foreach (ulong field in opened.Values)
            {
                ulong remaining = field;
                while (remaining != 0)
                {
                    remaining &= remaining - 1;
                    count++;
                }
            }
            return count;
        }

        private static Dictionary<int, ulong>
            ReadOpenedTreasureBits(
                string source,
                string key)
        {
            string temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "DragonSwordTreasureRadar");
            Directory.CreateDirectory(temporaryDirectory);
            string temporary = Path.Combine(
                temporaryDirectory,
                Guid.NewGuid().ToString("N") + ".db");
            File.Copy(source, temporary, true);

            IntPtr database = IntPtr.Zero;
            try
            {
                int result = NativeMethods.sqlite3_open_v2(
                    Utf8(temporary),
                    out database,
                    SqliteOpenReadOnly,
                    IntPtr.Zero);
                if (result != 0)
                {
                    throw SqliteError(
                        database,
                        result,
                        IntPtr.Zero);
                }

                return QueryOpenedTreasureBits(
                    database,
                    key);
            }
            finally
            {
                if (database != IntPtr.Zero)
                {
                    NativeMethods.sqlite3_close_v2(database);
                }
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // A stale temporary copy is safe to remove later.
                }
            }
        }

        private static Dictionary<int, ulong>
            QueryOpenedTreasureBits(
                IntPtr database,
                string key)
        {
            Dictionary<int, ulong> opened =
                new Dictionary<int, ulong>();
            string escapedKey = key.Replace("'", "''");
            string sql =
                "PRAGMA key = '" + escapedKey + "';" +
                "PRAGMA cipher_compatibility = 4;" +
                "SELECT CATEGORY,OPENED_BIT_FIELD " +
                "FROM tb_treasure_box;";
            NativeMethods.ExecCallback callback = delegate(
                IntPtr context,
                int count,
                IntPtr values,
                IntPtr names)
            {
                AddOpenedField(opened, count, values);
                return 0;
            };

            IntPtr error;
            int result = NativeMethods.sqlite3_exec(
                database,
                Utf8(sql),
                callback,
                IntPtr.Zero,
                out error);
            GC.KeepAlive(callback);
            if (result != 0)
            {
                Exception exception = SqliteError(
                    database,
                    result,
                    error);
                if (error != IntPtr.Zero)
                {
                    NativeMethods.sqlite3_free(error);
                }
                throw exception;
            }
            return opened;
        }

        private static void AddOpenedField(
            IDictionary<int, ulong> opened,
            int count,
            IntPtr values)
        {
            if (count < 2)
            {
                return;
            }

            int category;
            long signedField;
            string categoryText = PointerString(
                Marshal.ReadIntPtr(values, 0));
            string fieldText = PointerString(
                Marshal.ReadIntPtr(
                    values,
                    IntPtr.Size));
            if (int.TryParse(categoryText, out category)
                && long.TryParse(fieldText, out signedField))
            {
                opened[category] =
                    unchecked((ulong)signedField);
            }
        }

        private static Exception SqliteError(
            IntPtr database,
            int result,
            IntPtr error)
        {
            string message = error == IntPtr.Zero
                ? PointerString(
                    NativeMethods.sqlite3_errmsg(database))
                : PointerString(error);
            return new InvalidOperationException(
                "SQLCipher error " + result + ": " + message);
        }

        private static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

        private static string PointerString(IntPtr pointer)
        {
            return pointer == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringAnsi(pointer) ??
                    string.Empty;
        }

        private static ulong ReadUInt64(
            IntPtr process,
            ulong address)
        {
            return BitConverter.ToUInt64(
                ReadBytes(process, address, 8),
                0);
        }

        private static int ReadInt32(
            IntPtr process,
            ulong address)
        {
            return BitConverter.ToInt32(
                ReadBytes(process, address, 4),
                0);
        }

        private static byte[] ReadBytes(
            IntPtr process,
            ulong address,
            int size)
        {
            byte[] bytes = new byte[size];
            IntPtr read;
            if (!NativeMethods.ReadProcessMemory(
                    process,
                    new IntPtr(unchecked((long)address)),
                    bytes,
                    new IntPtr(size),
                    out read)
                || read.ToInt64() != size)
            {
                throw new InvalidOperationException(
                    "ReadProcessMemory failed at 0x" +
                    address.ToString("X") + ": " +
                    Marshal.GetLastWin32Error());
            }
            return bytes;
        }
    }
}
