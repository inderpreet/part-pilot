namespace InventoryTracker.Tests;

/// <summary>Controllable <see cref="TimeProvider"/> for deterministic timestamp tests.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
