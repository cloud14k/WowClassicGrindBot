namespace Core;

/// <summary>
/// Consumes a value from a TimedQueue-backed pixel cell once.
///
/// DataToColor keeps the current queue value visible for several addon ticks.
/// Zero is the idle/separator value. The addon emits a zero separator when two
/// adjacent logical queue items have the same encoded value, so equal items are
/// still distinguishable from one held pixel value.
/// </summary>
internal sealed class QueueValueTracker
{
    private int lastValue;
    private bool hasValue;

    public bool TryConsume(int value)
    {
        if (value == 0)
        {
            hasValue = false;
            return false;
        }

        if (hasValue && value == lastValue)
            return false;

        lastValue = value;
        hasValue = true;
        return true;
    }

    public void Reset()
    {
        lastValue = 0;
        hasValue = false;
    }
}
