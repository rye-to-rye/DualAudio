namespace DualAudio.Platform.Tests;

/// <summary>
/// A [Fact] that only runs when DUALAUDIO_HW_TESTS=1: it needs a real Bluetooth adapter and a paired phone
/// with Bluetooth on, in range. Combine with [Trait("Category", "Hardware")].
/// </summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "DUALAUDIO_HW_TESTS";

    public HardwareFactAttribute()
    {
        if (!IsEnabled)
        {
            Skip = $"Hardware test: set {EnvironmentVariable}=1 (needs a Bluetooth adapter and a paired phone in range).";
        }
    }

    public static bool IsEnabled => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";
}
