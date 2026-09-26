namespace Core.Goals;

/// <summary>
/// Executes the shared, throttled interact approach used by both the normal
/// approach goal and its timeout recovery.
/// </summary>
public sealed class ApproachExecutor(
    ConfigurableInput input,
    Wait wait,
    AddonBits bits,
    PlayerReader playerReader,
    ApproachThrottle approachThrottle)
{
    public void ResetForNewChase() => approachThrottle.ResetForNewChase();

    public bool TryPress(bool arrived)
    {
        if (!approachThrottle.ShouldPress(arrived) ||
            (bits.SoftInteract() && !HasValidSoftInteract()))
        {
            return false;
        }

        input.PressApproach();
        wait.Update();
        approachThrottle.OnPressed();
        return true;
    }

    private bool HasValidSoftInteract() =>
        bits.SoftInteract() &&
        !bits.SoftInteract_Dead() &&
        !bits.SoftInteract_Tagged() &&
        playerReader.SoftInteract_Type == GuidType.Creature;
}
