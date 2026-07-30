using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DragonSwordTreasureRadar
{
    internal sealed class SaveDatabaseKeyReader
    {
        private const uint ProcessReadAccess =
            0x0010 | 0x1000;
        private const ulong CurrentOwnerPointerRva =
            0x94DEAE8;
        private const ulong LegacyOwnerPointerRva =
            0x94DDB20;

        private static readonly byte[] OwnerReferencePattern =
        {
            0x48, 0x8B, 0x0D, 0, 0, 0, 0,
            0xE8, 0, 0, 0, 0,
            0x8B, 0xC7, 0x48, 0x8B, 0x5C, 0x24,
            0x40, 0x48, 0x8B, 0x6C, 0x24, 0x50,
        };

        private const string OwnerReferenceMask =
            "xxx????x????xxxxxxxxxxxx";

        private int _processId;
        private ulong _ownerPointerRva;
        private string _key;

        public void Reset()
        {
            _processId = 0;
            _ownerPointerRva = 0;
            _key = null;
        }

        public string Read(Process game)
        {
            if (_processId == game.Id
                && !string.IsNullOrEmpty(_key))
            {
                return _key;
            }

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
                Exception lastError = null;
                foreach (ulong rva in CandidateRvas(game))
                {
                    try
                    {
                        string key = ReadAtRva(
                            process,
                            moduleBase,
                            rva);
                        _processId = game.Id;
                        _ownerPointerRva = rva;
                        _key = key;
                        return _key;
                    }
                    catch (Exception exception)
                    {
                        lastError = exception;
                    }
                }

                throw new InvalidOperationException(
                    "Save database key could not be located.",
                    lastError);
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        private System.Collections.Generic.IEnumerable<ulong>
            CandidateRvas(Process game)
        {
            if (_processId != game.Id)
            {
                _processId = game.Id;
                _ownerPointerRva = DetectOwnerPointerRva(
                    game.MainModule.FileName);
            }

            if (_ownerPointerRva != 0)
            {
                yield return _ownerPointerRva;
            }

            if (CurrentOwnerPointerRva != _ownerPointerRva)
            {
                yield return CurrentOwnerPointerRva;
            }
            if (LegacyOwnerPointerRva != _ownerPointerRva)
            {
                yield return LegacyOwnerPointerRva;
            }
        }

        private static string ReadAtRva(
            IntPtr process,
            ulong moduleBase,
            ulong ownerPointerRva)
        {
            ulong owner = ReadUInt64(
                process,
                moduleBase + ownerPointerRva);
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

            string key = Encoding.Unicode
                .GetString(ReadBytes(
                    process,
                    keyPointer,
                    keyLength * 2))
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

        private static ulong DetectOwnerPointerRva(
            string executablePath)
        {
            byte[] image = File.ReadAllBytes(executablePath);
            int peOffset = BitConverter.ToInt32(image, 0x3C);
            if (peOffset <= 0
                || peOffset + 24 > image.Length
                || BitConverter.ToUInt32(image, peOffset)
                    != 0x00004550)
            {
                return 0;
            }

            int sectionCount =
                BitConverter.ToUInt16(image, peOffset + 6);
            int optionalHeaderSize =
                BitConverter.ToUInt16(image, peOffset + 20);
            int sectionTable =
                peOffset + 24 + optionalHeaderSize;

            for (int index = 0;
                index < sectionCount;
                index++)
            {
                int header = sectionTable + index * 40;
                if (header < 0
                    || header + 40 > image.Length)
                {
                    break;
                }

                uint rawSize =
                    BitConverter.ToUInt32(image, header + 16);
                uint rawOffset =
                    BitConverter.ToUInt32(image, header + 20);
                uint virtualAddress =
                    BitConverter.ToUInt32(image, header + 12);
                int match = FindPattern(
                    image,
                    checked((int)rawOffset),
                    checked((int)rawSize));
                if (match < 0)
                {
                    continue;
                }

                int displacement =
                    BitConverter.ToInt32(image, match + 3);
                long instructionRva =
                    virtualAddress + match - rawOffset;
                return unchecked((ulong)(
                    instructionRva + 7 + displacement));
            }
            return 0;
        }

        private static int FindPattern(
            byte[] data,
            int start,
            int size)
        {
            int end = Math.Min(
                data.Length,
                start + size) -
                OwnerReferencePattern.Length;
            for (int offset = start;
                offset <= end;
                offset++)
            {
                bool matches = true;
                for (int index = 0;
                    index < OwnerReferencePattern.Length;
                    index++)
                {
                    if (OwnerReferenceMask[index] != '?'
                        && data[offset + index] !=
                            OwnerReferencePattern[index])
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                {
                    return offset;
                }
            }
            return -1;
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
