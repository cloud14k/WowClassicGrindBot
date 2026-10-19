using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace PPather.Navmesh;

/// <summary>Parser for TrinityCore's native MAPS .tilelist format.</summary>
public static class ForeverWdtTileList
{
    public const uint Version = 10;
    public const int TileCount = 64 * 64;
    public const int FileLength = 4 + sizeof(uint) + sizeof(uint) + TileCount;

    public sealed record Parsed(int Build, HashSet<(int X, int Y)> Adts);

    public static Parsed Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != FileLength || data[0] != (byte)'M' || data[1] != (byte)'A' ||
            data[2] != (byte)'P' || data[3] != (byte)'S')
        {
            throw new InvalidDataException("WDT .tilelist must contain the complete 4108-byte MAPS record.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, sizeof(uint)));
        uint build = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, sizeof(uint)));
        if (version != Version || build == 0)
        {
            throw new InvalidDataException($"WDT .tilelist has unsupported version {version} or invalid Build {build}.");
        }

        ReadOnlySpan<byte> bits = data.Slice(12, TileCount);
        HashSet<(int X, int Y)> adts = [];
        for (int bit = 0; bit < bits.Length; bit++)
        {
            if (bits[bit] is not ((byte)'0') and not ((byte)'1'))
            {
                throw new InvalidDataException($"WDT .tilelist has a non-binary ADT flag at bit {bit}.");
            }
        }

        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int bitIndex = y * 64 + x;
                if (bits[TileCount - 1 - bitIndex] == (byte)'1')
                {
                    adts.Add((x, y));
                }
            }
        }

        if (adts.Count == 0)
        {
            throw new InvalidDataException("WDT .tilelist contains no ADTs.");
        }

        return new Parsed(checked((int)build), adts);
    }
}
