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
        string excludedRelativePath = null)
    {
        Directory.CreateDirectory(destinationRoot);
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
            if (excludedRelativePath != null
                && string.Equals(
                    relativePath,
                    excludedRelativePath,
                    System.StringComparison.OrdinalIgnoreCase))
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
                missingLines.Add(sourceLine);
                existingSettings.Add(match.Groups[1].Value);
            }
        }
        if (missingLines.Count == 0)
        {
            return;
        }

        int closingBrace = existingLines.FindLastIndex(
            line => Regex.IsMatch(line, @"^\s*}\s*,?\s*$"));
        if (closingBrace < 0)
        {
            // Keep a custom or malformed config untouched rather than
            // replacing user settings.
            return;
        }

        existingLines.InsertRange(closingBrace, missingLines);
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
