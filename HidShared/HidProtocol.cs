using System;

namespace HidShared;

/// <summary>CDC transport framing shared by the bot backend, CoreTests, and HidTester.</summary>
public static class HidProtocol
{
    public const byte KeyboardReportId = 1;
    public const byte MouseReportId = 2;
    public const byte FrameStart = 0xA5;
    public const byte AckStart = 0x5A;
    public const int KeyboardPayloadLength = 8;
    public const int MousePayloadLength = 5;

    public static byte[] Encode(byte reportId, byte sequence, ReadOnlySpan<byte> payload)
    {
        int expected = reportId switch
        {
            KeyboardReportId => KeyboardPayloadLength,
            MouseReportId => MousePayloadLength,
            _ => throw new ArgumentOutOfRangeException(nameof(reportId))
        };
        if (payload.Length != expected) throw new ArgumentException($"Report {reportId} must contain {expected} bytes.", nameof(payload));
        byte[] frame = new byte[payload.Length + 5];
        frame[0] = FrameStart; frame[1] = reportId; frame[2] = sequence; frame[3] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(4));
        for (int i = 0; i < frame.Length - 1; i++) frame[^1] ^= frame[i];
        return frame;
    }
}
