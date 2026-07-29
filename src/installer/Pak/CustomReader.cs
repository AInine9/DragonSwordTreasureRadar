using System;
using System.IO;
using System.Text;

internal sealed class CustomReader
{
    private readonly byte[] _data;
    private readonly byte _stringMask;
    private readonly byte _numberMask;

    public CustomReader(
        byte[] data,
        byte stringMask,
        byte numberMask)
    {
        _data = data;
        _stringMask = stringMask;
        _numberMask = numberMask;
    }

    public int Position { get; set; }

    public byte[] ReadRaw(int count)
    {
        if (count < 0 || Position > _data.Length - count)
        {
            throw new EndOfStreamException();
        }

        byte[] result = new byte[count];
        Buffer.BlockCopy(
            _data,
            Position,
            result,
            0,
            count);
        Position += count;
        return result;
    }

    public uint ReadUInt32()
    {
        uint value = BitConverter.ToUInt32(ReadRaw(4), 0);
        uint mask = _numberMask * 0x01010101u;
        return value ^ mask;
    }

    public int ReadInt32()
    {
        return unchecked((int)ReadUInt32());
    }

    public ulong ReadUInt64()
    {
        ulong value = BitConverter.ToUInt64(ReadRaw(8), 0);
        ulong mask =
            _numberMask * 0x0101010101010101ul;
        return value ^ mask;
    }

    public string ReadString()
    {
        return ReadStringWithMask(_stringMask);
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
            bytes[length - suffix.Length - 1] ^
            (byte)suffix[0]);
        return DecodeUtf8(bytes, inferredMask);
    }

    private string ReadStringWithMask(byte mask)
    {
        int length = ReadInt32();
        if (length == 0)
        {
            return string.Empty;
        }
        if (length < 0)
        {
            int byteCount = checked(-length * 2);
            byte[] bytes = ReadRaw(byteCount);
            ApplyMask(bytes, mask);
            string value = Encoding.Unicode.GetString(bytes);
            int terminator = value.IndexOf('\0');
            return terminator < 0
                ? value
                : value.Substring(0, terminator);
        }
        return DecodeUtf8(ReadRaw(length), mask);
    }

    private static string DecodeUtf8(
        byte[] bytes,
        byte mask)
    {
        ApplyMask(bytes, mask);
        int terminator = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(
            bytes,
            0,
            terminator < 0 ? bytes.Length : terminator);
    }

    private static void ApplyMask(byte[] bytes, byte mask)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] ^= mask;
        }
    }
}
