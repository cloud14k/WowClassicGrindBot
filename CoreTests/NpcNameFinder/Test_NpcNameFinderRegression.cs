using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.NpcFinder;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using System;

namespace CoreTests;

/// <summary>
/// Deterministic regression coverage for the final NpcNameFinder candidate count.
/// The line segments are supplied directly; this does not duplicate image
/// recognition or color matching.
/// </summary>
internal static class Test_NpcNameFinderRegression
{
    public static void Run(ILogger logger)
    {
        int failures = 0;

        using Image<Bgra32> image = new(1920, 1080);
        FakeScreenImageProvider screen = new(image);
        FakeLineSegmentProvider provider = new();
        using NpcResetEvent resetEvent = new();
        NpcNameFinder finder = new(logger, screen, resetEvent, provider);

        failures += Check(logger, finder, provider, [], 0, "zero");
        failures += Check(logger, finder, provider,
            [new LineSegment(100, 130, 300)], 1, "one");
        failures += Check(logger, finder, provider,
            [
                new LineSegment(100, 130, 300),
                new LineSegment(500, 530, 500)
            ], 2, "two");

        if (failures == 0)
        {
            Console.WriteLine("NpcNameFinder regression: PASS");
            Environment.ExitCode = 0;
        }
        else
        {
            Console.WriteLine($"NpcNameFinder regression: FAIL ({failures})");
            Environment.ExitCode = 1;
        }
    }

    private static int Check(
        ILogger logger,
        NpcNameFinder finder,
        FakeLineSegmentProvider provider,
        LineSegment[] segments,
        int expected,
        string name)
    {
        provider.Segments = segments;
        finder.Update();

        int actual = finder.NpcCount;
        logger.LogInformation(
            "NpcNameFinder regression {Case}: expected={Expected} actual={Actual}",
            name,
            expected,
            actual);

        return actual == expected ? 0 : 1;
    }

    private sealed class FakeScreenImageProvider : IScreenImageProvider
    {
        public FakeScreenImageProvider(Image<Bgra32> image)
        {
            ScreenImage = image;
        }

        public Image<Bgra32> ScreenImage { get; }

        public Rectangle ScreenRect => new(0, 0, ScreenImage.Width, ScreenImage.Height);
    }

    private sealed class FakeLineSegmentProvider : INpcLineSegmentProvider
    {
        public LineSegment[] Segments { get; set; } = [];

        public ReadOnlySpan<LineSegment> GetLineSegments(
            Rectangle area,
            float minLength,
            float minEndLength) => Segments;
    }
}
