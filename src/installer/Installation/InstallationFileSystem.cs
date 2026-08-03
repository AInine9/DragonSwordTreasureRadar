using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class InstallationFileSystem
{
    public static void CopyDirectory(
        string sourceRoot,
        string destinationRoot,
        params string[] excludedRelativePaths)
    {
        Directory.CreateDirectory(destinationRoot);
        HashSet<string> exclusions = new HashSet<string>(
            excludedRelativePaths ?? new string[0],
            System.StringComparer.OrdinalIgnoreCase);
        foreach (string sourceFile in Directory.EnumerateFiles(
            sourceRoot,
            "*",
            SearchOption.AllDirectories))
        {
            string relativePath = sourceFile
                .Substring(sourceRoot.Length)
                .TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            if (exclusions.Contains(relativePath))
            {
                continue;
            }
            string destinationFile = Path.Combine(
                destinationRoot,
                relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationFile));
            File.Copy(sourceFile, destinationFile, true);
        }
    }

    public static void CopyFileIfMissing(
        string sourceFile,
        string destinationFile)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(destinationFile));
        if (!File.Exists(destinationFile))
        {
            File.Copy(sourceFile, destinationFile, false);
        }
    }

    public static void InstallConfig(
        string sourceFile,
        string destinationFile)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(destinationFile));
        if (!File.Exists(destinationFile))
        {
            File.Copy(sourceFile, destinationFile, false);
            return;
        }

        Regex setting = new Regex(
            @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=",
            RegexOptions.CultureInvariant);
        List<string> existingLines =
            File.ReadAllLines(destinationFile).ToList();

        // These timings are implementation details rather than user-facing
        // behavior. Remove the former public settings during upgrades while
        // preserving all other custom configuration values.
        HashSet<string> deprecatedSettings = new HashSet<string>(
            new[]
            {
                "world_map_update_interval_ms",
                "map_load_resume_delay_ms"
            },
            System.StringComparer.OrdinalIgnoreCase);
        int lineCountBeforeMigration = existingLines.Count;
        existingLines.RemoveAll(line =>
        {
            string trimmed = line.Trim();
            if (trimmed.Equals(
                "-- Stability tuning for world-map polling and post-load recovery.",
                System.StringComparison.Ordinal))
            {
                return true;
            }
            Match deprecatedMatch = setting.Match(
                RemoveLuaComment(line));
            return deprecatedMatch.Success
                && deprecatedSettings.Contains(
                    deprecatedMatch.Groups[1].Value);
        });
        bool configChanged =
            existingLines.Count != lineCountBeforeMigration;

        HashSet<string> existingSettings = new HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (string sourceLine in existingLines)
        {
            string line = RemoveLuaComment(sourceLine);
            Match match = setting.Match(line);
            if (match.Success)
            {
                existingSettings.Add(match.Groups[1].Value);
            }
        }

        List<string> missingLines = new List<string>();
        foreach (string sourceLine in File.ReadAllLines(sourceFile))
        {
            Match match = setting.Match(
                RemoveLuaComment(sourceLine));
            if (match.Success
                && !existingSettings.Contains(match.Groups[1].Value))
            {
                string settingName = match.Groups[1].Value;
                if (settingName.Equals(
                    "show_height",
                    System.StringComparison.OrdinalIgnoreCase))
                {
                    missingLines.Add(
                        "    -- Show a 180-degree height pointer for the nearest treasure.");
                }
                else if (settingName.Equals(
                    "text_scale",
                    System.StringComparison.OrdinalIgnoreCase))
                {
                    missingLines.Add(
                        "    -- Scale treasure type and debug coordinate text (1.0 = default).");
                }
                missingLines.Add(sourceLine);
                existingSettings.Add(settingName);
            }
        }

        if (missingLines.Count > 0)
        {
            int closingBrace = existingLines.FindLastIndex(
                line => Regex.IsMatch(line, @"^\s*}\s*,?\s*$"));
            if (closingBrace < 0)
            {
                // Keep a custom or malformed config untouched rather than
                // replacing user settings.
                return;
            }

            existingLines.InsertRange(closingBrace, missingLines);
            configChanged = true;
        }

        if (!configChanged)
        {
            return;
        }

        WriteAllLinesAtomic(
            destinationFile,
            existingLines,
            new UTF8Encoding(false));
    }

    private static string RemoveLuaComment(string line)
    {
        int comment = line.IndexOf(
            "--",
            System.StringComparison.Ordinal);
        return comment < 0
            ? line
            : line.Substring(0, comment);
    }

    private static void WriteAllLinesAtomic(
        string path,
        IEnumerable<string> lines,
        Encoding encoding)
    {
        string temporaryPath = path + "." +
            Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporaryPath, lines, encoding);
            try
            {
                File.Replace(temporaryPath, path, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temporaryPath, path, true);
            }
            catch (IOException)
            {
                File.Copy(temporaryPath, path, true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void EnableMod(
        string modsFile,
        string modName)
    {
        string enabledLine = modName + " : 1";
        List<string> lines = File.Exists(modsFile)
            ? File.ReadAllLines(modsFile).ToList()
            : new List<string>();
        Regex existing = new Regex(
            @"^\s*" + Regex.Escape(modName) + @"\s*:",
            RegexOptions.IgnoreCase);
        int existingIndex = lines.FindIndex(
            line => existing.IsMatch(line));
        if (existingIndex >= 0)
        {
            lines[existingIndex] = enabledLine;
        }
        else
        {
            lines.Add(enabledLine);
        }

        File.WriteAllLines(
            modsFile,
            lines,
            new UTF8Encoding(false));
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Cleanup failure does not invalidate the installation.
        }
    }
}
