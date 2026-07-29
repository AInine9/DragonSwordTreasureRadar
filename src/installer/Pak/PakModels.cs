using System.Collections.Generic;

internal sealed class PakFooter
{
    public bool Encrypted;
    public ulong IndexOffset;
    public ulong IndexSize;
}

internal sealed class EncodedEntry
{
    public int CompressionSlot;
    public bool Encrypted;
    public uint CompressionBlockCount;
    public uint CompressionBlockSize;
    public ulong Offset;
    public ulong UncompressedSize;
    public ulong CompressedSize;
}

internal sealed class DataBlock
{
    public ulong Start;
    public ulong End;
}

internal sealed class DataEntry
{
    public ulong PakOffset;
    public ulong Offset;
    public ulong CompressedSize;
    public ulong UncompressedSize;
    public uint CompressionIndex;
    public byte Flags;
    public uint CompressionBlockSize;
    public readonly List<DataBlock> Blocks =
        new List<DataBlock>();
}
