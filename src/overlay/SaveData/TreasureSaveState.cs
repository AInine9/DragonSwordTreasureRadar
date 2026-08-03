using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DragonSwordTreasureRadar
{
    internal sealed class TreasureSaveState
    {
        private const int SqliteOpenReadOnly = 0x00000001;

        private static readonly TimeSpan RefreshInterval =
            TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan DatabaseDiscoveryInterval =
            TimeSpan.FromSeconds(5);

        private readonly object _sync = new object();
        private readonly Dictionary<int, ulong> _opened =
            new Dictionary<int, ulong>();
        private readonly SaveDatabaseKeyReader _keyReader =
            new SaveDatabaseKeyReader();
        private readonly TreasureOverrides _overrides =
            new TreasureOverrides();

        private DateTime _nextRefreshUtc;
        private DateTime _nextDatabaseDiscoveryUtc;
        private string _selectedDatabasePath;
        private string _databaseCandidateSummary;
        private string _lastDatabasePath;
        private DateTime _lastDatabaseWriteUtc;
        private string _lastKey;
        private string _lastError;
        private string _lastDatabaseAttemptLog;
        private string _lastDatabaseSuccessLog;
        private int _gameProcessId;
        private int _version;
        private bool _hasLoadedSaveState;
        private bool _loadInProgress;
        private int _lastOverrideVersion = -1;

        public int GameProcessId
        {
            get { return _gameProcessId; }
        }

        public bool HasLoadedSaveState
        {
            get
            {
                lock (_sync)
                {
                    return _hasLoadedSaveState;
                }
            }
        }

        public int Version
        {
            get
            {
                lock (_sync)
                {
                    return _version;
                }
            }
        }

        public string DatabaseName
        {
            get
            {
                lock (_sync)
                {
                    return _lastDatabasePath == null
                        ? "none"
                        : SafeSlotName(_lastDatabasePath);
                }
            }
        }

        public string DatabaseWriteSummary
        {
            get
            {
                lock (_sync)
                {
                    return _lastDatabaseWriteUtc ==
                        DateTime.MinValue
                        ? "none"
                        : _lastDatabaseWriteUtc.ToString(
                            "O",
                            CultureInfo.InvariantCulture);
                }
            }
        }

        public int OpenedBitCount
        {
            get
            {
                lock (_sync)
                {
                    return CountOpenedBits(_opened);
                }
            }
        }

        public string LastErrorSummary
        {
            get
            {
                lock (_sync)
                {
                    return _lastError ?? "none";
                }
            }
        }

        public bool IsOpened(long saveId)
        {
            if (saveId <= 0)
            {
                return false;
            }

            bool ignored;
            saveId = _overrides.Resolve(
                saveId,
                out ignored);
            if (ignored)
            {
                return true;
            }

            int category = (int)(saveId / 64);
            int bit = (int)(saveId % 64);
            lock (_sync)
            {
                ulong field;
                return _opened.TryGetValue(category, out field)
                    && (field & (1UL << bit)) != 0;
            }
        }

        public string Describe(long sourceSaveId)
        {
            if (sourceSaveId <= 0)
            {
                return "invalidId";
            }

            bool ignored;
            long resolvedSaveId = _overrides.Resolve(
                sourceSaveId,
                out ignored);

            int sourceCategory =
                (int)(sourceSaveId / 64);
            int sourceBit =
                (int)(sourceSaveId % 64);
            int resolvedCategory =
                (int)(resolvedSaveId / 64);
            int resolvedBit =
                (int)(resolvedSaveId % 64);

            lock (_sync)
            {
                ulong sourceField;
                bool hasSourceField = _opened.TryGetValue(
                    sourceCategory,
                    out sourceField);
                bool sourceOpened = hasSourceField
                    && (sourceField &
                        (1UL << sourceBit)) != 0;

                ulong resolvedField;
                bool hasResolvedField = _opened.TryGetValue(
                    resolvedCategory,
                    out resolvedField);
                bool resolvedOpened = ignored
                    || (hasResolvedField
                        && (resolvedField &
                            (1UL << resolvedBit)) != 0);

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "source={0}; resolved={1}; ignored={2}; " +
                    "sourceCategory={3}; sourceBit={4}; " +
                    "sourceField={5}; sourceOpened={6}; " +
                    "resolvedCategory={7}; resolvedBit={8}; " +
                    "resolvedField={9}; opened={10}",
                    sourceSaveId,
                    resolvedSaveId,
                    ignored,
                    sourceCategory,
                    sourceBit,
                    hasSourceField
                        ? "0x" + sourceField.ToString("X16")
                        : "missing",
                    sourceOpened,
                    resolvedCategory,
                    resolvedBit,
                    hasResolvedField
                        ? "0x" + resolvedField.ToString("X16")
                        : "missing",
                    resolvedOpened);
            }
        }

        public void Refresh()
        {
            _overrides.Refresh();
            int overrideVersion = _overrides.Version;
            lock (_sync)
            {
                if (overrideVersion != _lastOverrideVersion)
                {
                    _lastOverrideVersion = overrideVersion;
                    _version++;
                }
            }

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
            string key = _keyReader.Read(game);
            string candidateSummary;
            string databasePath = FindNewestSaveDatabaseCached(
                game,
                out candidateSummary);
            LogDatabaseSelection(
                databasePath,
                candidateSummary);

            DateTime writeTime =
                File.GetLastWriteTimeUtc(databasePath);
            lock (_sync)
            {
                if (databasePath == _lastDatabasePath
                    && writeTime == _lastDatabaseWriteUtc
                    && key == _lastKey)
                {
                    return;
                }
                if (_loadInProgress)
                {
                    return;
                }
                _loadInProgress = true;
            }

            SaveLoadRequest request = new SaveLoadRequest
            {
                GameProcessId = game.Id,
                DatabasePath = databasePath,
                DatabaseWriteUtc = writeTime,
                Key = key,
            };
            if (!ThreadPool.QueueUserWorkItem(
                    LoadSaveState,
                    request))
            {
                lock (_sync)
                {
                    _loadInProgress = false;
                }
                throw new InvalidOperationException(
                    "Could not queue save-state refresh.");
            }
        }

        private void LoadSaveState(object state)
        {
            SaveLoadRequest request =
                (SaveLoadRequest)state;
            try
            {
                Dictionary<int, ulong> opened =
                    ReadOpenedTreasureBits(
                        request.DatabasePath,
                        request.Key);
                lock (_sync)
                {
                    if (_gameProcessId !=
                        request.GameProcessId)
                    {
                        return;
                    }

                    List<long> newlyOpened =
                        FindNewlySetIds(
                            _opened,
                            opened);
                    List<long> newlyClosed =
                        FindNewlySetIds(
                            opened,
                            _opened);

                    _opened.Clear();
                    foreach (KeyValuePair<int, ulong> pair
                        in opened)
                    {
                        _opened[pair.Key] = pair.Value;
                    }
                    _lastDatabasePath =
                        request.DatabasePath;
                    _lastDatabaseWriteUtc =
                        request.DatabaseWriteUtc;
                    _lastKey = request.Key;
                    _lastError = null;
                    _hasLoadedSaveState = true;
                    _version++;
                    LogDatabaseLoaded(
                        request.DatabasePath,
                        opened);
                    LogDatabaseDelta(
                        request.DatabasePath,
                        newlyOpened,
                        newlyClosed);
                }
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    if (_gameProcessId ==
                        request.GameProcessId)
                    {
                        LogRefreshError(exception);
                    }
                }
            }
            finally
            {
                lock (_sync)
                {
                    _loadInProgress = false;
                }
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

        private void LogDatabaseDelta(
            string databasePath,
            IList<long> newlyOpened,
            IList<long> newlyClosed)
        {
            if (!DebugSettings.Enabled
                || (newlyOpened.Count == 0
                    && newlyClosed.Count == 0))
            {
                return;
            }

            ErrorLog.WriteDebug(
                "Save-state bit delta: database=" +
                SafeSlotName(databasePath) +
                "; newlyOpened=" +
                FormatIdList(newlyOpened) +
                "; newlyClosed=" +
                FormatIdList(newlyClosed));
        }

        private void LogRefreshError(Exception exception)
        {
            string message =
                exception.GetType().FullName + ": " +
                exception.Message;
            lock (_sync)
            {
                if (message != _lastError)
                {
                    _lastError = message;
                    ErrorLog.Write(
                        "Save-state refresh failed",
                        exception);
                }
            }
        }

        private void ResetForGameProcess(int processId)
        {
            if (_gameProcessId == processId)
            {
                return;
            }

            lock (_sync)
            {
                _gameProcessId = processId;
                _opened.Clear();
                _nextDatabaseDiscoveryUtc =
                    DateTime.MinValue;
                _selectedDatabasePath = null;
                _databaseCandidateSummary = null;
                _lastDatabasePath = null;
                _lastDatabaseWriteUtc = DateTime.MinValue;
                _lastKey = null;
                _lastError = null;
                _lastDatabaseAttemptLog = null;
                _lastDatabaseSuccessLog = null;
                _hasLoadedSaveState = false;
                _loadInProgress = false;
                _version++;
            }
            _keyReader.Reset();
        }

        private string FindNewestSaveDatabaseCached(
            Process game,
            out string candidateSummary)
        {
            if (_selectedDatabasePath != null
                && File.Exists(_selectedDatabasePath)
                && DateTime.UtcNow <
                    _nextDatabaseDiscoveryUtc)
            {
                candidateSummary =
                    _databaseCandidateSummary;
                return _selectedDatabasePath;
            }

            _selectedDatabasePath =
                FindNewestSaveDatabase(
                    game,
                    out _databaseCandidateSummary);
            _nextDatabaseDiscoveryUtc =
                DateTime.UtcNow.Add(
                    DatabaseDiscoveryInterval);
            candidateSummary = _databaseCandidateSummary;
            return _selectedDatabasePath;
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

            IGrouping<string, string> activeSlot = ordered
                .GroupBy(
                    path => Path.Combine(
                        Path.GetDirectoryName(path),
                        Path.GetFileNameWithoutExtension(path)),
                    StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group =>
                    group.Max(path =>
                        File.GetLastWriteTimeUtc(path)))
                .First();

            // The game may write the live treasure state to either
            // the .db or the .bak file. Always use the newest file in
            // the active slot instead of forcing .db, otherwise newly
            // opened chests can remain visible indefinitely.
            return activeSlot
                .OrderByDescending(
                    path => File.GetLastWriteTimeUtc(path))
                .First();
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

        private static List<long> FindNewlySetIds(
            IDictionary<int, ulong> previous,
            IDictionary<int, ulong> current)
        {
            HashSet<int> categories =
                new HashSet<int>(previous.Keys);
            categories.UnionWith(current.Keys);

            List<long> result = new List<long>();
            foreach (int category in categories)
            {
                ulong previousField;
                if (!previous.TryGetValue(
                    category,
                    out previousField))
                {
                    previousField = 0;
                }

                ulong currentField;
                if (!current.TryGetValue(
                    category,
                    out currentField))
                {
                    currentField = 0;
                }

                ulong newlySet =
                    currentField & ~previousField;
                for (int bit = 0; bit < 64; bit++)
                {
                    if ((newlySet &
                        (1UL << bit)) != 0)
                    {
                        result.Add(
                            category * 64L + bit);
                    }
                }
            }

            result.Sort();
            return result;
        }

        private static string FormatIdList(
            IList<long> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return "none";
            }

            const int limit = 40;
            string[] values = ids
                .Take(limit)
                .Select(id => id.ToString(
                    CultureInfo.InvariantCulture))
                .ToArray();
            string result = string.Join(",", values);
            return ids.Count > limit
                ? result + ",...(" +
                    ids.Count.ToString(
                        CultureInfo.InvariantCulture) +
                    " total)"
                : result;
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

        private sealed class SaveLoadRequest
        {
            public int GameProcessId;
            public string DatabasePath;
            public DateTime DatabaseWriteUtc;
            public string Key;
        }

    }
}
