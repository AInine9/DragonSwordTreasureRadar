using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class InstallationFileSystem
{
    public static void CopyDirectory(
        string sourceRoot,
        string destinationRoot)
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
            string destinationFile = Path.Combine(
                destinationRoot,
                relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationFile));
            File.Copy(sourceFile, destinationFile, true);
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
