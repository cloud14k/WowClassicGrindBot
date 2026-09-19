using Newtonsoft.Json;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

using System;
using System.IO;

namespace Core;

public static class FrameConfigMeta
{
    // Version 5 stores coordinates derived from the addon's frame geometry.
    // Version 4 could contain coordinates inferred from obstructed pixels.
    public const int Version = 6;
    public const int TopOffset = 16;
    public const string DefaultFilename = "frame_config.json";
}

public static class FrameConfig
{
    public static bool Exists()
    {
        return File.Exists(FrameConfigMeta.DefaultFilename);
    }

    public static bool IsValid(Rectangle rect, Version addonVersion)
    {
        try
        {
            var config = Load();

            bool sameVersion = config.Version == FrameConfigMeta.Version;
            bool sameAddonVersion = config.AddonVersion == addonVersion;
            bool sameRect = config.Rect.Width == rect.Width && config.Rect.Height == rect.Height;
            return sameAddonVersion && sameVersion && sameRect && config.Frames.Length > 1;
        }
        catch
        {
            return false;
        }
    }

    public static DataFrameConfig Load()
    {
        return JsonConvert.DeserializeObject<DataFrameConfig>(File.ReadAllText(FrameConfigMeta.DefaultFilename));
    }

    public static DataFrame[] LoadFrames()
    {
        if (Exists())
        {
            var config = Load();
            if (config.Version == FrameConfigMeta.Version)
                return config.Frames;
        }

        return Array.Empty<DataFrame>();
    }

    public static DataFrameMeta LoadMeta()
    {
        var config = Load();
        if (config.Version == FrameConfigMeta.Version)
            return config.Meta;

        return DataFrameMeta.Empty;
    }

    public static void Save(Rectangle rect, Version addonVersion, DataFrameMeta meta, DataFrame[] dataFrames)
    {
        DataFrameConfig config = new(FrameConfigMeta.Version, addonVersion, rect, meta, dataFrames);

        string json = JsonConvert.SerializeObject(config);
        File.WriteAllText(FrameConfigMeta.DefaultFilename, json);
    }

    public static void Delete()
    {
        if (Exists())
        {
            File.Delete(FrameConfigMeta.DefaultFilename);
        }
    }

    public static DataFrameMeta GetMeta(Bgra32 color)
    {
        int hash = color.R * 65536 + color.G * 256 + color.B;
        if (hash == 0)
            return DataFrameMeta.Empty;

        // CELL_SPACING * 10000000 + CELL_SIZE * 100000 + 1000 * FRAME_ROWS + NUMBER_OF_FRAMES
        int spacing = hash / 10000000;
        int size = hash / 100000 % 100;
        int rows = hash / 1000 % 100;
        int count = hash % 1000;

        return new DataFrameMeta(hash, spacing, size, rows, count);
    }

    public static DataFrame[] CreateFrames(DataFrameMeta meta, Image<Bgra32> bmp)
    {
        if (meta.Count <= 1 || meta.Rows <= 0 || meta.Sizes <= 0 || meta.Spacing < 0)
            return Array.Empty<DataFrame>();

        DataFrame[] frames = new DataFrame[meta.Count];

        // The addon uses WoW UI coordinates, which are scaled before WGC sees
        // them. In setup mode every frame has a unique exact colour, so locate
        // the physical screen pixels instead of deriving screen coordinates
        // from CELL_SIZE/CELL_SPACING.
        if (!TryFindMetadataPoint(meta, bmp, out int metadataX, out int metadataY))
            return Array.Empty<DataFrame>();

        frames[0] = new(0, metadataX, metadataY);

        int previousX = metadataX;
        for (int i = 1; i < frames.Length; i++)
        {
            if (!TryFindIndexPoint(bmp, i, previousX, metadataY, out int x, out int y))
                return Array.Empty<DataFrame>();

            frames[i] = new(i, x, y);
            previousX = x;
        }

        return frames;
    }

    private static bool TryFindMetadataPoint(
        DataFrameMeta meta, Image<Bgra32> bmp, out int x, out int y)
    {
        int maxX = Math.Min(bmp.Width, 512);
        int maxY = Math.Min(bmp.Height, 128);

        for (int yi = 0; yi < maxY; yi++)
        {
            ReadOnlySpan<Bgra32> row = bmp.DangerousGetPixelRowMemory(yi).Span;
            for (int xi = 0; xi < maxX; xi++)
            {
                if (GetMeta(row[xi]).Hash == meta.Hash)
                {
                    x = xi;
                    y = yi;
                    return true;
                }
            }
        }

        x = y = -1;
        return false;
    }

    private static bool TryFindIndexPoint(
        Image<Bgra32> bmp, int index, int startX, int targetY, out int x, out int y)
    {
        int minY = Math.Max(0, targetY - 32);
        int maxY = Math.Min(bmp.Height, targetY + 33);
        byte blue = (byte)index;

        for (int xi = Math.Max(0, startX); xi < bmp.Width; xi++)
        {
            for (int yi = minY; yi < maxY; yi++)
            {
                Bgra32 pixel = bmp[xi, yi];
                if (pixel.R == 0 && pixel.G == 0 && pixel.B == blue)
                {
                    x = xi;
                    y = yi;
                    return true;
                }
            }
        }

        x = y = -1;
        return false;
    }
}
