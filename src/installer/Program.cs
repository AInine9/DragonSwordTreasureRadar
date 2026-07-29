using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;

internal static class Program
{
    private const uint PakMagic = 0x5A6F12E1;
    private const uint DragonSwordPakVersion = 101;
    private const int PakFooterSize = 221;
    private const string TargetMountPoint =
        "../../../DS/Content/__GeneratedGameData__/Server/XML/GameData/";
    private const string TargetFileName = "SectionTreasureBoxData.xml";
    private const string ModName = "DragonSwordTreasureMap";
    private const string XmlNamespace =
        "http://leigient.549n.com/schema/SectionActorInfo";

    private sealed class PakFooter
    {
        public bool Encrypted;
        public ulong IndexOffset;
        public ulong IndexSize;
    }

    private sealed class EncodedEntry
    {
        public int CompressionSlot;
        public bool Encrypted;
        public uint CompressionBlockCount;
        public uint CompressionBlockSize;
        public ulong Offset;
        public ulong UncompressedSize;
        public ulong CompressedSize;
    }

    private sealed class DataBlock
    {
        public ulong Start;
        public ulong End;
    }

    private sealed class DataEntry
    {
        public ulong PakOffset;
        public ulong Offset;
        public ulong CompressedSize;
        public ulong UncompressedSize;
        public uint CompressionIndex;
        public byte Flags;
        public uint CompressionBlockSize;
        public readonly List<DataBlock> Blocks = new List<DataBlock>();
    }

    private sealed class CustomReader
    {
        private readonly byte[] data;
        private readonly byte stringMask;
        private readonly byte numberMask;

        public int Position { get; set; }

        public CustomReader(
            byte[] data, byte stringMask, byte numberMask)
        {
            this.data = data;
            this.stringMask = stringMask;
            this.numberMask = numberMask;
        }

        public byte[] ReadRaw(int count)
        {
            if (count < 0 || Position > data.Length - count)
            {
                throw new EndOfStreamException();
            }
            byte[] result = new byte[count];
            Buffer.BlockCopy(data, Position, result, 0, count);
            Position += count;
            return result;
        }

        public uint ReadUInt32()
        {
            uint value = BitConverter.ToUInt32(ReadRaw(4), 0);
            uint mask = numberMask * 0x01010101u;
            return value ^ mask;
        }

        public int ReadInt32()
        {
            return unchecked((int)ReadUInt32());
        }

        public ulong ReadUInt64()
        {
            ulong value = BitConverter.ToUInt64(ReadRaw(8), 0);
            ulong mask = numberMask * 0x0101010101010101ul;
            return value ^ mask;
        }

        public string ReadString()
        {
            return ReadStringWithMask(stringMask);
        }

        public string ReadStringEndingWith(string suffix)
        {
            int savedPosition = Position;
            int length = ReadInt32();
            if (length <= suffix.Length || length < 0)
            {
                Position = savedPosition;
                return ReadString();
            }

            byte[] bytes = ReadRaw(length);
            byte inferredMask = (byte)(
                bytes[length - suffix.Length - 1] ^ (byte)suffix[0]);
            return DecodeUtf8(bytes, inferredMask);
        }

        private string ReadStringWithMask(byte mask)
        {
            int length = ReadInt32();
            if (length == 0)
            {
                return String.Empty;
            }
            if (length < 0)
            {
                int byteCount = checked(-length * 2);
                byte[] unicodeBytes = ReadRaw(byteCount);
                for (int index = 0; index < unicodeBytes.Length; index++)
                {
                    unicodeBytes[index] ^= mask;
                }
                string unicode = Encoding.Unicode.GetString(unicodeBytes);
                int terminator = unicode.IndexOf('\0');
                return terminator < 0
                    ? unicode
                    : unicode.Substring(0, terminator);
            }
            return DecodeUtf8(ReadRaw(length), mask);
        }

        private static string DecodeUtf8(byte[] bytes, byte mask)
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] ^= mask;
            }
            int zero = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(
                bytes, 0, zero < 0 ? bytes.Length : zero);
        }
    }

    private static class AesKeyFinder
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

        public static IEnumerable<byte[]> Find(string executablePath)
        {
            byte[] executable = File.ReadAllBytes(executablePath);
            HashSet<string> seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            for (int patternIndex = 0;
                patternIndex < Patterns.Length;
                patternIndex++)
            {
                byte?[] mask = Compile(Patterns[patternIndex]);
                foreach (int offset in FindMatches(executable, mask))
                {
                    byte[] key = ReadKey(
                        executable, offset, DwordOffsets[patternIndex]);
                    string hex = BitConverter.ToString(key);
                    if (seen.Add(hex))
                    {
                        yield return key;
                    }
                }
            }
        }

        private static byte?[] Compile(string pattern)
        {
            string[] tokens = pattern.Split(
                new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
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
            byte[] data, byte?[] mask)
        {
            int anchor = Array.FindIndex(
                mask, value => value.HasValue);
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
                for (int index = 0; index < mask.Length; index++)
                {
                    if (mask[index].HasValue
                        && data[offset + index] != mask[index].Value)
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
            byte[] data, int offset, int[] positions)
        {
            byte[] key = new byte[32];
            for (int index = 0; index < positions.Length; index++)
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

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool silent = args.Any(argument =>
            argument.Equals(
                "--silent", StringComparison.OrdinalIgnoreCase));
        try
        {
            string selectedPath = args.FirstOrDefault(argument =>
                !argument.StartsWith("--", StringComparison.Ordinal));
            if (selectedPath == null)
            {
                selectedPath = SelectGameExecutable();
            }
            if (String.IsNullOrWhiteSpace(selectedPath))
            {
                return 1;
            }

            string gameRoot = ResolveGameRoot(selectedPath);
            string ue4ssRoot = ResolveUe4ssRoot(gameRoot);
            string applicationRoot = AppDomain.CurrentDomain.BaseDirectory;
            string payloadRoot = Path.Combine(
                applicationRoot, "payload", ModName);
            string oozPath = Path.Combine(
                applicationRoot, "tools", "ooz.exe");

            ValidateInstallerFiles(payloadRoot, oozPath);
            int treasureCount = Install(
                gameRoot, ue4ssRoot, payloadRoot, oozPath);

            string successMessage = String.Format(
                CultureInfo.InvariantCulture,
                "Installation completed successfully.\n\n" +
                "Generated {0} treasure locations from the game PAK.\n" +
                "Installed to:\n{1}",
                treasureCount,
                Path.Combine(
                    ue4ssRoot,
                    "Mods",
                    ModName));
            if (silent)
            {
                Console.WriteLine(successMessage);
            }
            else
            {
                MessageBox.Show(
                    successMessage,
                    "DragonSword Treasure Radar",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return 0;
        }
        catch (Exception error)
        {
            if (silent)
            {
                Console.Error.WriteLine(error);
            }
            else
            {
                MessageBox.Show(
                    error.Message,
                    "DragonSword Treasure Radar - Installation failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static string SelectGameExecutable()
    {
        using (OpenFileDialog dialog = new OpenFileDialog())
        {
            dialog.Title =
                "Select DSClient-Win64-Shipping.exe";
            dialog.Filter =
                "DragonSword game executable " +
                "(DSClient-Win64-Shipping.exe)|" +
                "DSClient-Win64-Shipping.exe|" +
                "Executable files (*.exe)|*.exe";
            dialog.FileName = "DSClient-Win64-Shipping.exe";
            dialog.CheckFileExists = true;
            dialog.CheckPathExists = true;
            dialog.Multiselect = false;
            return dialog.ShowDialog() == DialogResult.OK
                ? dialog.FileName
                : null;
        }
    }

    private static string ResolveGameRoot(string selectedPath)
    {
        string fullPath = Path.GetFullPath(
            selectedPath.Trim().Trim('"'));
        if (File.Exists(fullPath))
        {
            fullPath = Path.GetDirectoryName(fullPath);
        }
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                "The selected path does not exist.");
        }

        DirectoryInfo candidate = new DirectoryInfo(fullPath);
        for (int level = 0; level < 12 && candidate != null; level++)
        {
            string executable = Path.Combine(
                candidate.FullName,
                "DS",
                "Binaries",
                "Win64",
                "DSClient-Win64-Shipping.exe");
            string pak = Path.Combine(
                candidate.FullName,
                "DS",
                "Content",
                "Paks",
                "pakchunk109-WindowsClient.pak");
            if (File.Exists(executable) && File.Exists(pak))
            {
                return candidate.FullName;
            }
            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "The selected folder is not a supported DragonSword " +
            "Awakening installation.");
    }

    private static string ResolveUe4ssRoot(string gameRoot)
    {
        string win64Root = Path.Combine(
            gameRoot, "DS", "Binaries", "Win64");
        List<string> candidates = new List<string>();
        candidates.Add(Path.Combine(win64Root, "ue4ss"));
        candidates.Add(win64Root);

        try
        {
            candidates.AddRange(
                Directory.EnumerateDirectories(
                    win64Root, "*", SearchOption.TopDirectoryOnly));
        }
        catch (UnauthorizedAccessException)
        {
        }

        string detected = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.Exists(
                Path.Combine(path, "UE4SS.dll")))
            .OrderByDescending(path =>
                ScoreUe4ssRoot(path, win64Root))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (detected != null)
        {
            return detected;
        }

        throw new DirectoryNotFoundException(
            "UE4SS was not found. Install UE4SS before this mod.\n\n" +
            "The installer checked both supported layouts:\n" +
            Path.Combine(win64Root, "UE4SS.dll") + "\n" +
            Path.Combine(win64Root, "ue4ss", "UE4SS.dll"));
    }

    private static int ScoreUe4ssRoot(
        string candidate, string win64Root)
    {
        int score = 0;
        string expectedSubfolder = Path.Combine(win64Root, "ue4ss");
        if (candidate.Equals(
            expectedSubfolder, StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }
        if (File.Exists(Path.Combine(
            candidate, "Mods", "mods.txt")))
        {
            score += 200;
        }
        if (File.Exists(Path.Combine(
            candidate, "UE4SS-settings.ini")))
        {
            score += 100;
        }
        if (Directory.Exists(Path.Combine(candidate, "Mods")))
        {
            score += 50;
        }
        if (candidate.Equals(
            win64Root, StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }
        return score;
    }

    private static void ValidateInstallerFiles(
        string payloadRoot, string oozPath)
    {
        if (!Directory.Exists(payloadRoot))
        {
            throw new DirectoryNotFoundException(
                "The installer payload folder is missing.");
        }
        if (!File.Exists(oozPath))
        {
            throw new FileNotFoundException(
                "The bundled Oodle decompressor is missing.", oozPath);
        }
    }

    private static int Install(
        string gameRoot,
        string ue4ssRoot,
        string payloadRoot,
        string oozPath)
    {
        string win64Root = Path.Combine(
            gameRoot, "DS", "Binaries", "Win64");
        string executablePath = Path.Combine(
            win64Root, "DSClient-Win64-Shipping.exe");
        string pakPath = Path.Combine(
            gameRoot,
            "DS",
            "Content",
            "Paks",
            "pakchunk109-WindowsClient.pak");
        string modsRoot = Path.Combine(ue4ssRoot, "Mods");
        Directory.CreateDirectory(modsRoot);

        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "DragonSwordTreasureRadar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            byte[] aesKey = FindWorkingAesKey(
                executablePath, pakPath);
            string xmlPath = Path.Combine(
                temporaryRoot, TargetFileName);
            ExtractTreasureXml(
                pakPath, aesKey, oozPath, temporaryRoot, xmlPath);

            string generatedLua = Path.Combine(
                temporaryRoot, "treasures.lua");
            int treasureCount = GenerateTreasuresLua(
                xmlPath, generatedLua);

            string targetModRoot = Path.Combine(modsRoot, ModName);
            CopyDirectory(payloadRoot, targetModRoot);
            string scriptsRoot = Path.Combine(
                targetModRoot, "scripts");
            Directory.CreateDirectory(scriptsRoot);
            File.Copy(
                generatedLua,
                Path.Combine(scriptsRoot, "treasures.lua"),
                true);
            EnableMod(Path.Combine(modsRoot, "mods.txt"));
            return treasureCount;
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private static byte[] FindWorkingAesKey(
        string executablePath, string pakPath)
    {
        foreach (byte[] candidate in AesKeyFinder.Find(executablePath))
        {
            if (CanReadTargetPak(pakPath, candidate))
            {
                return candidate;
            }
        }
        throw new InvalidDataException(
            "The PAK encryption key could not be detected. " +
            "The game version may not be supported.");
    }

    private static bool CanReadTargetPak(
        string pakPath, byte[] key)
    {
        try
        {
            using (FileStream stream = File.OpenRead(pakPath))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                PakFooter footer = ReadFooter(reader);
                byte[] index = ReadAt(
                    reader, footer.IndexOffset, footer.IndexSize);
                if (footer.Encrypted)
                {
                    index = DecryptAes(index, key);
                }
                byte numberMask = InferNumberMask(index);
                byte stringMask = (byte)(index[4] ^ (byte)'.');
                CustomReader indexReader = new CustomReader(
                    index, stringMask, numberMask);
                return indexReader.ReadString().Equals(
                    TargetMountPoint,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractTreasureXml(
        string pakPath,
        byte[] key,
        string oozPath,
        string temporaryRoot,
        string outputPath)
    {
        using (FileStream stream = File.OpenRead(pakPath))
        using (BinaryReader binary = new BinaryReader(stream))
        {
            PakFooter footer = ReadFooter(binary);
            byte[] index = ReadAt(
                binary, footer.IndexOffset, footer.IndexSize);
            if (footer.Encrypted)
            {
                index = DecryptAes(index, key);
            }

            byte numberMask = InferNumberMask(index);
            byte stringMask = (byte)(index[4] ^ (byte)'.');
            CustomReader main = new CustomReader(
                index, stringMask, numberMask);
            string mountPoint = main.ReadString();
            if (!mountPoint.Equals(
                TargetMountPoint,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The generated game-data PAK has an unexpected mount point.");
            }

            main.ReadUInt32();
            main.ReadUInt64();
            uint hasPathHashIndex = main.ReadUInt32();
            if (hasPathHashIndex != 0)
            {
                main.ReadUInt64();
                main.ReadUInt64();
                main.ReadRaw(20);
            }

            uint hasDirectoryIndex = main.ReadUInt32();
            if (hasDirectoryIndex == 0)
            {
                throw new InvalidDataException(
                    "The PAK directory index is missing.");
            }
            ulong directoryOffset = main.ReadUInt64();
            ulong directorySize = main.ReadUInt64();
            main.ReadRaw(20);
            uint encodedEntriesSize = main.ReadUInt32();
            byte[] encodedEntries = main.ReadRaw(
                checked((int)encodedEntriesSize));

            byte[] directoryIndex = ReadAt(
                binary, directoryOffset, directorySize);
            if (footer.Encrypted)
            {
                directoryIndex = DecryptAes(directoryIndex, key);
            }
            byte directoryStringMask = directoryIndex.Length > 8
                ? (byte)(directoryIndex[8] ^ (byte)'/')
                : stringMask;
            CustomReader directory = new CustomReader(
                directoryIndex,
                directoryStringMask,
                numberMask);

            int encodedOffset = FindTargetEncodedOffset(directory);
            if (encodedOffset < 0)
            {
                throw new InvalidDataException(
                    "The treasure XML uses an unsupported PAK entry layout.");
            }

            EncodedEntry encoded = FindEncodedEntry(
                encodedEntries, encodedOffset, binary);
            DataEntry data = ReadDataEntry(binary, encoded.Offset);
            ValidateEntry(encoded, data, stream.Length);
            WriteDecompressedEntry(
                binary,
                data,
                key,
                oozPath,
                temporaryRoot,
                outputPath);
        }
    }

    private static int FindTargetEncodedOffset(
        CustomReader directory)
    {
        uint directoryCount = directory.ReadUInt32();
        if (directoryCount > 100000)
        {
            throw new InvalidDataException(
                "The PAK directory count is invalid.");
        }

        for (uint directoryIndex = 0;
            directoryIndex < directoryCount;
            directoryIndex++)
        {
            directory.ReadString();
            uint fileCount = directory.ReadUInt32();
            if (fileCount > 1000000)
            {
                throw new InvalidDataException(
                    "The PAK file count is invalid.");
            }

            for (uint fileIndex = 0;
                fileIndex < fileCount;
                fileIndex++)
            {
                string fileName = directory.ReadStringEndingWith(".xml");
                int encodedOffset = directory.ReadInt32();
                if (fileName.Equals(
                    TargetFileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return encodedOffset;
                }
            }
        }
        throw new FileNotFoundException(
            TargetFileName + " was not found in the game PAK.");
    }

    private static EncodedEntry FindEncodedEntry(
        byte[] encodedEntries,
        int encodedOffset,
        BinaryReader pakReader)
    {
        for (int mask = 0; mask <= Byte.MaxValue; mask++)
        {
            try
            {
                EncodedEntry candidate = ReadEncodedEntry(
                    encodedEntries, encodedOffset, (byte)mask);
                if (candidate.Offset >= (ulong)pakReader.BaseStream.Length
                    || candidate.UncompressedSize == 0
                    || candidate.UncompressedSize > 64 * 1024 * 1024
                    || candidate.CompressedSize == 0
                    || candidate.CompressedSize >
                        candidate.UncompressedSize
                    || candidate.CompressionBlockCount == 0
                    || candidate.CompressionBlockCount > 4096
                    || candidate.CompressionBlockSize == 0
                    || candidate.CompressionBlockSize > 4 * 1024 * 1024)
                {
                    continue;
                }

                DataEntry data = ReadDataEntry(
                    pakReader, candidate.Offset);
                if (data.CompressedSize == candidate.CompressedSize
                    && data.UncompressedSize == candidate.UncompressedSize
                    && data.CompressionIndex ==
                        (uint)(candidate.CompressionSlot + 1))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }
        throw new InvalidDataException(
            "The treasure XML entry could not be decoded.");
    }

    private static EncodedEntry ReadEncodedEntry(
        byte[] encodedEntries, int offset, byte numberMask)
    {
        CustomReader reader = new CustomReader(
            encodedEntries, 0, numberMask);
        reader.Position = offset;
        uint bits = reader.ReadUInt32();
        int compression = (int)((bits >> 23) & 0x3F);
        compression = compression == 0 ? -1 : compression - 1;
        bool encrypted = (bits & (1u << 22)) != 0;
        uint blockCount = (bits >> 6) & 0xFFFF;
        uint blockSize = bits & 0x3F;
        if (blockSize == 0x3F)
        {
            blockSize = reader.ReadUInt32();
        }
        else
        {
            blockSize <<= 11;
        }

        Func<int, ulong> readVariable = bit =>
            (bits & (1u << bit)) != 0
                ? reader.ReadUInt32()
                : reader.ReadUInt64();
        ulong dataOffset = readVariable(31);
        ulong uncompressed = readVariable(30);
        ulong compressed = compression < 0
            ? uncompressed
            : readVariable(29);

        if (blockCount > 1 || (blockCount > 0 && encrypted))
        {
            for (uint index = 0; index < blockCount; index++)
            {
                reader.ReadUInt32();
            }
        }

        EncodedEntry entry = new EncodedEntry();
        entry.CompressionSlot = compression;
        entry.Encrypted = encrypted;
        entry.CompressionBlockCount = blockCount;
        entry.CompressionBlockSize = blockSize;
        entry.Offset = dataOffset;
        entry.UncompressedSize = uncompressed;
        entry.CompressedSize = compressed;
        return entry;
    }

    private static DataEntry ReadDataEntry(
        BinaryReader reader, ulong entryOffset)
    {
        reader.BaseStream.Position = checked((long)entryOffset);
        DataEntry entry = new DataEntry();
        entry.PakOffset = entryOffset;
        entry.Offset = reader.ReadUInt64();
        entry.CompressedSize = reader.ReadUInt64();
        entry.UncompressedSize = reader.ReadUInt64();
        entry.CompressionIndex = reader.ReadUInt32();
        reader.ReadBytes(20);

        if (entry.CompressionIndex != 0)
        {
            uint blockCount = reader.ReadUInt32();
            if (blockCount == 0 || blockCount > 4096)
            {
                throw new InvalidDataException(
                    "The PAK compression block count is invalid.");
            }
            for (uint index = 0; index < blockCount; index++)
            {
                DataBlock block = new DataBlock();
                block.Start = reader.ReadUInt64();
                block.End = reader.ReadUInt64();
                entry.Blocks.Add(block);
            }
        }

        entry.Flags = reader.ReadByte();
        entry.CompressionBlockSize = reader.ReadUInt32();
        return entry;
    }

    private static void ValidateEntry(
        EncodedEntry encoded, DataEntry data, long pakLength)
    {
        if (data.Offset != 0
            || data.CompressedSize != encoded.CompressedSize
            || data.UncompressedSize != encoded.UncompressedSize
            || data.CompressionIndex !=
                (uint)(encoded.CompressionSlot + 1)
            || data.Blocks.Count != encoded.CompressionBlockCount
            || data.CompressionBlockSize !=
                encoded.CompressionBlockSize)
        {
            throw new InvalidDataException(
                "The PAK entry metadata is inconsistent.");
        }

        ulong totalCompressed = 0;
        foreach (DataBlock block in data.Blocks)
        {
            if (block.End <= block.Start
                || encoded.Offset + block.End > (ulong)pakLength)
            {
                throw new InvalidDataException(
                    "A PAK compression block is invalid.");
            }
            totalCompressed += block.End - block.Start;
        }
        if (totalCompressed != data.CompressedSize)
        {
            throw new InvalidDataException(
                "The PAK compressed size is inconsistent.");
        }
    }

    private static void WriteDecompressedEntry(
        BinaryReader pak,
        DataEntry data,
        byte[] aesKey,
        string oozPath,
        string temporaryRoot,
        string outputPath)
    {
        if (data.CompressionIndex != 1)
        {
            throw new NotSupportedException(
                "The treasure XML is not using the expected Oodle compression.");
        }

        using (FileStream output = File.Create(outputPath))
        {
            ulong remaining = data.UncompressedSize;
            for (int index = 0; index < data.Blocks.Count; index++)
            {
                DataBlock block = data.Blocks[index];
                int compressedLength = checked(
                    (int)(block.End - block.Start));
                int storedLength = (data.Flags & 1) != 0
                    ? Align16(compressedLength)
                    : compressedLength;
                pak.BaseStream.Position = checked(
                    (long)(block.Start + data.PakOffset));
                byte[] compressed = pak.ReadBytes(storedLength);
                if (compressed.Length != storedLength)
                {
                    throw new EndOfStreamException();
                }
                if ((data.Flags & 1) != 0)
                {
                    compressed = DecryptAes(compressed, aesKey)
                        .Take(compressedLength)
                        .ToArray();
                }

                int rawLength = checked((int)Math.Min(
                    remaining, data.CompressionBlockSize));
                string packedPath = Path.Combine(
                    temporaryRoot,
                    String.Format(
                        CultureInfo.InvariantCulture,
                        "block-{0}.ooz",
                        index));
                string rawPath = Path.Combine(
                    temporaryRoot,
                    String.Format(
                        CultureInfo.InvariantCulture,
                        "block-{0}.raw",
                        index));
                using (FileStream blockFile = File.Create(packedPath))
                using (BinaryWriter writer = new BinaryWriter(blockFile))
                {
                    writer.Write((ulong)rawLength);
                    writer.Write(compressed);
                }

                RunOoz(oozPath, packedPath, rawPath);
                using (FileStream raw = File.OpenRead(rawPath))
                {
                    if (raw.Length != rawLength)
                    {
                        throw new InvalidDataException(
                            "An Oodle block has an unexpected output size.");
                    }
                    raw.CopyTo(output);
                }
                remaining -= (ulong)rawLength;
                File.Delete(packedPath);
                File.Delete(rawPath);
            }

            if (remaining != 0
                || (ulong)output.Length != data.UncompressedSize)
            {
                throw new InvalidDataException(
                    "The treasure XML extraction is incomplete.");
            }
        }
    }

    private static void RunOoz(
        string oozPath, string packedPath, string rawPath)
    {
        ProcessStartInfo start = new ProcessStartInfo();
        start.FileName = oozPath;
        start.Arguments = "-q -f "
            + QuoteArgument(packedPath)
            + " "
            + QuoteArgument(rawPath);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardError = true;

        using (Process process = Process.Start(start))
        {
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(rawPath))
            {
                throw new InvalidDataException(
                    "Oodle decompression failed. " + error.Trim());
            }
        }
    }

    private static int GenerateTreasuresLua(
        string xmlPath, string outputPath)
    {
        XmlDocument document = new XmlDocument();
        document.XmlResolver = null;
        document.Load(xmlPath);
        XmlNodeList nodes = document.SelectNodes(
            "//*[local-name()='SectionActorData']");
        if (nodes == null || nodes.Count < 1000)
        {
            throw new InvalidDataException(
                "The extracted treasure data is incomplete.");
        }

        List<string> lines = new List<string>();
        lines.Add("return {");
        foreach (XmlNode node in nodes)
        {
            XmlElement element = node as XmlElement;
            if (element == null)
            {
                continue;
            }

            string cid = element.GetAttribute("CID", XmlNamespace);
            string section = element.GetAttribute(
                "SectionUID", XmlNamespace);
            string x = element.GetAttribute("PosX", XmlNamespace);
            string y = element.GetAttribute("PosY", XmlNamespace);
            ValidateInteger(cid, "CID");
            ValidateInteger(section, "SectionUID");
            ValidateNumber(x, "PosX");
            ValidateNumber(y, "PosY");
            lines.Add(String.Format(
                CultureInfo.InvariantCulture,
                "    {{ save_id = {0}, section = \"{1}\", " +
                "x = {2}, y = {3} }},",
                cid,
                section,
                x,
                y));
        }
        lines.Add("}");
        File.WriteAllLines(
            outputPath,
            lines,
            new UTF8Encoding(false));
        return nodes.Count;
    }

    private static void ValidateInteger(
        string value, string fieldName)
    {
        ulong ignored;
        if (!UInt64.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ignored))
        {
            throw new InvalidDataException(
                fieldName + " contains an invalid value.");
        }
    }

    private static void ValidateNumber(
        string value, string fieldName)
    {
        double ignored;
        if (!Double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out ignored)
            || Double.IsNaN(ignored)
            || Double.IsInfinity(ignored))
        {
            throw new InvalidDataException(
                fieldName + " contains an invalid value.");
        }
    }

    private static void CopyDirectory(
        string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(
            sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = sourceFile.Substring(
                sourceRoot.Length).TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            string destinationFile = Path.Combine(
                destinationRoot, relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationFile));
            File.Copy(sourceFile, destinationFile, true);
        }
    }

    private static void EnableMod(string modsFile)
    {
        const string enabledLine = ModName + " : 1";
        List<string> lines = File.Exists(modsFile)
            ? File.ReadAllLines(modsFile).ToList()
            : new List<string>();
        Regex existing = new Regex(
            @"^\s*" + Regex.Escape(ModName) + @"\s*:",
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

    private static PakFooter ReadFooter(BinaryReader reader)
    {
        if (reader.BaseStream.Length < PakFooterSize)
        {
            throw new InvalidDataException("The PAK is too small.");
        }
        reader.BaseStream.Position =
            reader.BaseStream.Length - PakFooterSize;
        reader.ReadBytes(16);
        bool encrypted = reader.ReadByte() != 0;
        uint magic = reader.ReadUInt32();
        uint version = reader.ReadUInt32();
        ulong indexOffset = reader.ReadUInt64();
        ulong indexSize = reader.ReadUInt64();
        if (magic != PakMagic || version != DragonSwordPakVersion)
        {
            throw new InvalidDataException(
                "The DragonSword PAK format is not supported.");
        }
        if (indexOffset + indexSize >
            (ulong)reader.BaseStream.Length)
        {
            throw new InvalidDataException(
                "The PAK index is outside the file.");
        }
        PakFooter footer = new PakFooter();
        footer.Encrypted = encrypted;
        footer.IndexOffset = indexOffset;
        footer.IndexSize = indexSize;
        return footer;
    }

    private static byte[] ReadAt(
        BinaryReader reader, ulong offset, ulong size)
    {
        if (size > Int32.MaxValue)
        {
            throw new InvalidDataException("The PAK index is too large.");
        }
        reader.BaseStream.Position = checked((long)offset);
        byte[] result = reader.ReadBytes(checked((int)size));
        if ((ulong)result.Length != size)
        {
            throw new EndOfStreamException();
        }
        return result;
    }

    private static byte[] DecryptAes(
        byte[] encrypted, byte[] key)
    {
        if (encrypted.Length % 16 != 0)
        {
            throw new InvalidDataException(
                "Encrypted PAK data is not AES aligned.");
        }
        using (Aes aes = Aes.Create())
        {
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            using (ICryptoTransform decryptor = aes.CreateDecryptor())
            {
                return decryptor.TransformFinalBlock(
                    encrypted, 0, encrypted.Length);
            }
        }
    }

    private static byte InferNumberMask(byte[] index)
    {
        if (index.Length < 8
            || index[1] != index[2]
            || index[2] != index[3])
        {
            throw new InvalidDataException(
                "The DragonSword PAK index mask is invalid.");
        }
        return index[1];
    }

    private static int Align16(int value)
    {
        return checked((value + 15) & ~15);
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static void TryDeleteDirectory(string path)
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
        }
    }
}
