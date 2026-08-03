using System;
using System.Text.RegularExpressions;

namespace DragonSwordTreasureRadar
{
    internal enum TreasureKind
    {
        Other,
        Unlock,
        Monster,
        Dig,
        Key,
        Map,
        MiniGame,
        PressurePuzzle,
        StatuePuzzle
    }

    // Converts locally generated UIDName values into short, stable labels
    // used by the overlay and treasure_overrides.txt.
    internal static class TreasureIdentity
    {
        private static readonly Regex DataTableNamePattern =
            new Regex(
                @"^DT_([A-Za-z]+)_G[0-9]+_(.+)$",
                RegexOptions.Compiled
                | RegexOptions.CultureInvariant
                | RegexOptions.IgnoreCase);

        public static TreasureKind GetKind(string uidName)
        {
            string category;
            string identifier;
            if (!TrySplit(uidName, out category, out identifier))
            {
                return TreasureKind.Other;
            }

            if (category.Equals(
                "Unlock",
                StringComparison.OrdinalIgnoreCase))
            {
                return TreasureKind.Unlock;
            }
            if (category.Equals(
                "Monster",
                StringComparison.OrdinalIgnoreCase))
            {
                return TreasureKind.Monster;
            }
            if (category.Equals(
                "Dig",
                StringComparison.OrdinalIgnoreCase))
            {
                return TreasureKind.Dig;
            }
            if (category.Equals(
                "Key",
                StringComparison.OrdinalIgnoreCase))
            {
                return TreasureKind.Key;
            }
            if (category.Equals(
                "Map",
                StringComparison.OrdinalIgnoreCase))
            {
                return TreasureKind.Map;
            }
            if (category.Equals(
                "MiniGame",
                StringComparison.OrdinalIgnoreCase))
            {
                return TreasureKind.MiniGame;
            }
            if (category.Equals(
                "PressurePuzzle",
                StringComparison.OrdinalIgnoreCase))
            {
                return TreasureKind.PressurePuzzle;
            }
            if (category.Equals(
                "StatuePuzzle",
                StringComparison.OrdinalIgnoreCase))
            {
                return TreasureKind.StatuePuzzle;
            }
            return TreasureKind.Other;
        }

        public static string GetDebugName(
            string uidName,
            long saveId)
        {
            string category;
            string identifier;
            if (TrySplit(uidName, out category, out identifier))
            {
                return GetCategoryCode(category)
                    + "_" + identifier;
            }

            return "TB_" + saveId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string GetCategoryCode(string category)
        {
            if (category.Equals(
                "Unlock",
                StringComparison.OrdinalIgnoreCase))
            {
                return "U";
            }
            if (category.Equals(
                "Monster",
                StringComparison.OrdinalIgnoreCase))
            {
                return "M";
            }
            if (category.Equals(
                "Dig",
                StringComparison.OrdinalIgnoreCase))
            {
                return "D";
            }
            if (category.Equals(
                "Key",
                StringComparison.OrdinalIgnoreCase))
            {
                return "K";
            }
            if (category.Equals(
                "Map",
                StringComparison.OrdinalIgnoreCase))
            {
                return "MAP";
            }
            if (category.Equals(
                "MiniGame",
                StringComparison.OrdinalIgnoreCase))
            {
                return "MG";
            }
            if (category.Equals(
                "PressurePuzzle",
                StringComparison.OrdinalIgnoreCase))
            {
                return "PP";
            }
            if (category.Equals(
                "StatuePuzzle",
                StringComparison.OrdinalIgnoreCase))
            {
                return "SP";
            }

            string trimmed = category == null
                ? String.Empty
                : category.Trim();
            if (trimmed.Length == 0)
            {
                return "TB";
            }
            return trimmed.Length <= 4
                ? trimmed.ToUpperInvariant()
                : trimmed.Substring(0, 4).ToUpperInvariant();
        }

        private static bool TrySplit(
            string uidName,
            out string category,
            out string identifier)
        {
            category = null;
            identifier = null;
            if (String.IsNullOrWhiteSpace(uidName))
            {
                return false;
            }

            Match match = DataTableNamePattern.Match(
                uidName.Trim());
            if (!match.Success)
            {
                return false;
            }

            category = match.Groups[1].Value;
            identifier = match.Groups[2].Value;
            return category.Length > 0
                && identifier.Length > 0;
        }
    }
}
