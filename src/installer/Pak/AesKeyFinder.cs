using System;
using System.Collections.Generic;
using System.IO;

internal static class AesKeyFinder
{
    private static readonly string[] Patterns =
    {
        "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?",
        "C7 ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?",
        "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? 48 ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ?",
        "C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? ? C7 ? ? ? ? ? C3"
    };

    private static readonly int[][] DwordOffsets =
    {
        new[] { 3, 10, 17, 24, 35, 42, 49, 56 },
        new[] { 2, 9, 16, 23, 30, 37, 44, 51 },
        new[] { 3, 10, 21, 28, 35, 42, 49, 56 },
        new[] { 51, 45, 38, 31, 24, 17, 10, 3 }
    };

    public static IEnumerable<byte[]> Find(
        string executablePath)
    {
        byte[] executable =
            File.ReadAllBytes(executablePath);
        HashSet<string> seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        for (int patternIndex = 0;
            patternIndex < Patterns.Length;
            patternIndex++)
        {
            byte?[] mask = Compile(Patterns[patternIndex]);
            foreach (int offset in FindMatches(
                executable,
                mask))
            {
                byte[] key = ReadKey(
                    executable,
                    offset,
                    DwordOffsets[patternIndex]);
                if (seen.Add(BitConverter.ToString(key)))
                {
                    yield return key;
                }
            }
        }
    }

    private static byte?[] Compile(string pattern)
    {
        string[] tokens = pattern.Split(
            new[] { ' ' },
            StringSplitOptions.RemoveEmptyEntries);
        byte?[] result = new byte?[tokens.Length];
        for (int index = 0; index < tokens.Length; index++)
        {
            result[index] = tokens[index] == "?"
                ? (byte?)null
                : Convert.ToByte(tokens[index], 16);
        }
        return result;
    }

    private static IEnumerable<int> FindMatches(
        byte[] data,
        byte?[] mask)
    {
        int anchor = Array.FindIndex(
            mask,
            value => value.HasValue);
        byte expected = mask[anchor].Value;

        for (int offset = 0;
            offset <= data.Length - mask.Length;
            offset++)
        {
            if (data[offset + anchor] != expected)
            {
                continue;
            }

            bool matched = true;
            for (int index = 0;
                index < mask.Length;
                index++)
            {
                if (mask[index].HasValue
                    && data[offset + index] !=
                        mask[index].Value)
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                yield return offset;
            }
        }
    }

    private static byte[] ReadKey(
        byte[] data,
        int offset,
        int[] positions)
    {
        byte[] key = new byte[32];
        for (int index = 0;
            index < positions.Length;
            index++)
        {
            Buffer.BlockCopy(
                data,
                offset + positions[index],
                key,
                index * 4,
                4);
        }
        return key;
    }
}
