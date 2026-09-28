using System;
using System.Diagnostics;
using System.Collections.Generic;
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
        // Limit probing to the save owner's small header, never the process heap.
        private const int OwnerHeaderSize = 0x400;
        private ulong[] _candidateRvas;
        private string _validatedDatabase;

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
        private string _key;

        public void Reset()
        {
            _processId = 0;
            _key = null;
            _candidateRvas = null;
            _validatedDatabase = null;
        }

        public string Read(Process game, string databasePath, Action<string> validateKey)
        {
            if (_processId == game.Id
                && !string.IsNullOrEmpty(_key)
                && string.Equals(_validatedDatabase, databasePath, StringComparison.OrdinalIgnoreCase))
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
                        ulong owner = ReadUInt64(process, moduleBase + rva);
                        if (!IsUserPointer(owner))
                        {
                            throw new InvalidOperationException("Save database owner is not ready.");
                        }
                        HashSet<string> tested = new HashSet<string>();
                        for (int offset = 0; offset <= OwnerHeaderSize - 16; offset += 8)
                        {
                            string key;
                            try
                            {
                                key = ReadKeyField(process, owner + (ulong)offset);
                            }
                            catch (InvalidOperationException)
                            {
                                continue;
                            }
                            if (!tested.Add(key)) continue;
                            try
                            {
                                // Printable text is only a candidate. Verify against a read-only
                                // copy of the selected database before caching or using it.
                                validateKey(key);
                            }
                            catch (Exception exception)
                            {
                                lastError = exception;
                                continue;
                            }
                            _processId = game.Id;
                            _key = key;
                            _validatedDatabase = databasePath;
                            ErrorLog.WriteMessage("Save database key validated: owner RVA=0x" +
                                rva.ToString("X") + ", field offset=0x" + offset.ToString("X"));
                            return _key;
                        }
                    }
                    catch (Exception exception)
                    {
                        lastError = exception;
                    }
                }

                throw new InvalidOperationException(
                    "Save database key could not be located or validated (owner candidates: " +
                    (_candidateRvas == null ? 0 : _candidateRvas.Length) + ").",
                    lastError);
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }

        private IEnumerable<ulong> CandidateRvas(Process game)
        {
            if (_processId != game.Id || _candidateRvas == null)
            {
                Reset();
                _processId = game.Id;
                _candidateRvas = DetectOwnerPointerRvas(game.MainModule.FileName);
            }
            foreach (ulong rva in _candidateRvas) yield return rva;
        }

        private static bool IsUserPointer(ulong pointer)
        {
            return pointer >= 0x10000 && pointer < 0x0000800000000000;
        }

        private static string ReadKeyField(IntPtr process, ulong address)
        {
            byte[] field = ReadBytes(process, address, 16);
            ulong keyPointer = BitConverter.ToUInt64(field, 0);
            int keyLength = BitConverter.ToInt32(field, 8);
            int capacity = BitConverter.ToInt32(field, 12);
            if (!IsUserPointer(keyPointer) || keyLength <= 1 || keyLength > 256
                || capacity < keyLength || capacity > 4096)
            {
                throw new InvalidOperationException("Not a valid save key string field.");
            }
            byte[] value = ReadBytes(process, keyPointer, keyLength * 2);
            if (value[value.Length - 1] != 0 || value[value.Length - 2] != 0)
            {
                throw new InvalidOperationException("Save key string is not terminated.");
            }
            string key = Encoding.Unicode
                .GetString(value)
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

        private static ulong[] DetectOwnerPointerRvas(
            string executablePath)
        {
            byte[] image = File.ReadAllBytes(executablePath);
            List<ulong> candidates = new List<ulong>();
            if (image.Length < 64) return candidates.ToArray();
            int peOffset = BitConverter.ToInt32(image, 0x3C);
            if (peOffset <= 0
                || peOffset + 24 > image.Length
                || BitConverter.ToUInt32(image, peOffset)
                    != 0x00004550)
            {
                return candidates.ToArray();
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
                uint flags = BitConverter.ToUInt32(image, header + 36);
                if ((flags & 0x20000000) == 0 || rawOffset >= image.Length) continue;
                int start = checked((int)rawOffset);
                int end = (int)Math.Min((long)image.Length, (long)rawOffset + rawSize);
                while (start < end)
                {
                    int match = FindPattern(image, start, end - start);
                    if (match < 0) break;
                    int displacement = BitConverter.ToInt32(image, match + 3);
                    long instructionRva = (long)virtualAddress + match - rawOffset;
                    long target = instructionRva + 7 + displacement;
                    // Owner globals must fall in writable image data, not code or a stale RVA.
                    for (int section = 0; section < sectionCount; section++)
                    {
                        int h = sectionTable + section * 40;
                        if (h < 0 || h + 40 > image.Length) break;
                        uint va = BitConverter.ToUInt32(image, h + 12);
                        uint length = BitConverter.ToUInt32(image, h + 8);
                        uint attributes = BitConverter.ToUInt32(image, h + 36);
                        if ((attributes & 0x80000000) != 0 && target >= va
                            && target + 8 <= (long)va + length && !candidates.Contains((ulong)target))
                            candidates.Add((ulong)target);
                    }
                    start = match + 1;
                }
            }
            return candidates.ToArray();
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
