using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharedLib.NpcFinder;

public sealed class CpuLineSegmentProvider : IConfigurableLineSegmentProvider, IDisposable
{
    private const int RESOLUTION = 16;

    private readonly IScreenImageProvider imageProvider;
    private readonly ArrayCounter counter = new();

    private SearchMode searchMode;
    private NpcNames nameType;

    private LineSegment[]? segments;

    public CpuLineSegmentProvider(IScreenImageProvider imageProvider)
    {
        this.imageProvider = imageProvider;
    }

    public void Configure(SearchMode searchMode, NpcNames nameType)
    {
        this.searchMode = searchMode;
        this.nameType = nameType;
    }

    [SkipLocalsInit]
    public ReadOnlySpan<LineSegment> GetLineSegments(
        Rectangle area, float minLength, float minEndLength)
    {
        // NpcNameFinder consumes the previous span synchronously before asking
        // for the next frame. Return the old buffer only at that boundary.
        ReleaseSegments();

        int rowSize = (area.Right - area.Left) / RESOLUTION;
        int height = (area.Bottom - area.Top) / RESOLUTION;
        int totalSize = rowSize * height;

        segments = ArrayPool<LineSegment>.Shared.Rent(totalSize);

        Rectangle rectangle = new(area.X, area.Y, area.Width, area.Height);
        Buffer2D<Bgra32> pixelBuffer = imageProvider.ScreenImage.Frames[0].PixelBuffer;

        counter.count = 0;

        switch (searchMode)
        {
            case SearchMode.Fuzzy:
                DispatchFuzzy(segments, rowSize, rectangle,
                    minLength, minEndLength, pixelBuffer);
                break;
            case SearchMode.Simple:
                DispatchSimple(segments, rowSize, rectangle,
                    minLength, minEndLength, pixelBuffer);
                break;
        }

        return new(segments, 0, Math.Min(segments.Length, counter.count));
    }

    public void Dispose()
    {
        ReleaseSegments();
    }

    private void ReleaseSegments()
    {
        if (segments is null)
            return;

        ArrayPool<LineSegment>.Shared.Return(segments);
        segments = null;
    }

    private void DispatchFuzzy(
        LineSegment[] segments, int rowSize, Rectangle rectangle,
        float minLength, float minEndLength, Buffer2D<Bgra32> pixelBuffer)
    {
        switch (nameType)
        {
            case NpcNames.Enemy | NpcNames.Neutral | NpcNames.NamePlate:
                RunOperation<FuzzyEnemyNeutralNamePlateMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Enemy | NpcNames.Neutral:
                RunOperation<FuzzyEnemyNeutralMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Friendly | NpcNames.Neutral:
                RunOperation<FuzzyFriendlyNeutralMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Enemy:
                RunOperation<FuzzyEnemyMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Friendly:
                RunOperation<FuzzyFriendlyMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Neutral:
                RunOperation<FuzzyNeutralMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Corpse:
                RunOperation<FuzzyCorpseMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.NamePlate:
                RunOperation<FuzzyNamePlateMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
        }
    }

    private void DispatchSimple(
        LineSegment[] segments, int rowSize, Rectangle rectangle,
        float minLength, float minEndLength, Buffer2D<Bgra32> pixelBuffer)
    {
        switch (nameType)
        {
            case NpcNames.Enemy | NpcNames.Neutral:
                RunOperation<SimpleEnemyNeutralMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Friendly | NpcNames.Neutral:
                RunOperation<SimpleFriendlyNeutralMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Enemy:
                RunOperation<SimpleEnemyMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Friendly:
                RunOperation<SimpleFriendlyMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Neutral:
                RunOperation<SimpleNeutralMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.Corpse:
                RunOperation<SimpleCorpseMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.NamePlate:
                RunOperation<SimpleNamePlateMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
            case NpcNames.None:
                RunOperation<NoMatchMatcher>(
                    segments, rowSize, rectangle, minLength, minEndLength, pixelBuffer);
                break;
        }
    }

    private void RunOperation<TMatcher>(
        LineSegment[] segments, int rowSize, Rectangle rectangle,
        float minLength, float minEndLength, Buffer2D<Bgra32> pixelBuffer)
        where TMatcher : struct, IColorMatcher
    {
        LineSegmentOperation<TMatcher> operation = new(
            segments,
            rowSize,
            rectangle,
            minLength,
            minEndLength,
            counter,
            default,
            pixelBuffer);

        ParallelRowIterator.IterateRows<LineSegmentOperation<TMatcher>, LineSegment>(
            Configuration.Default,
            rectangle,
            in operation);
    }
}
