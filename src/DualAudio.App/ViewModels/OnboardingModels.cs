using DualAudio.Core.Abstractions;
using DualAudio.Core.Settings;

namespace DualAudio.App.ViewModels;

/// <summary>When the first-run window is shown. Pure, so it is unit-tested.</summary>
public static class OnboardingPolicy
{
    /// <summary>First run, or the saved phone was forgotten: there is nothing to auto-connect to.</summary>
    public static bool ShouldShow(AppSettings settings) => settings.PreferredDeviceId is null;

    /// <summary>"Skip" is offered only once onboarding has succeeded at least once.</summary>
    public static bool CanSkip(AppSettings settings) => settings.OnboardingCompleted;
}

public enum OnboardingStep
{
    Checks = 0,
    Pair = 1,
    PickPhone = 2,
    Connect = 3,
}

public enum OnboardingOutcome
{
    /// <summary>Phone audio arrived; the phone is saved.</summary>
    Completed,

    /// <summary>Skipped (only offered after an earlier success).</summary>
    Skipped,
}

/// <summary>The button on a failed check row.</summary>
public enum CheckFix
{
    None,
    OpenBluetoothSettings,

    /// <summary>Radio.SetStateAsync(On), only because the user clicked the button that says so.</summary>
    TurnOnBluetooth,
}

/// <summary>One green/red row of the capability check.</summary>
/// <param name="IsBlocking">A failed blocking row keeps "Next" disabled.</param>
public sealed record CheckRow(string Title, bool Passed, string Detail, CheckFix Fix, bool IsBlocking)
{
    public string? FixLabel => Passed ? null : Fix switch
    {
        CheckFix.OpenBluetoothSettings => "Open Bluetooth settings",
        CheckFix.TurnOnBluetooth => "Turn on Bluetooth",
        _ => null,
    };

    public bool HasFix => FixLabel is not null;

    public static IReadOnlyList<CheckRow> From(CapabilityReport report)
    {
        var rows = new List<CheckRow>
        {
            report.IsOsSupported
                ? new("Windows version", true, $"Windows build {report.OsBuild} can receive phone audio.", CheckFix.None, true)
                : new(
                    "Windows version",
                    false,
                    $"DualAudio needs Windows 10 version 2004 (build {CapabilityReport.MinimumOsBuild}) or later. This PC has build {report.OsBuild}. Update Windows, then check again.",
                    CheckFix.None,
                    true),
            report.HasRadio
                ? new("Bluetooth adapter", true, "This PC has Bluetooth.", CheckFix.None, true)
                : new("Bluetooth adapter", false, "No Bluetooth adapter found. Plug in or enable a Bluetooth adapter, then check again.", CheckFix.OpenBluetoothSettings, true),
        };

        if (report.HasRadio)
        {
            rows.Add(report.Radio switch
            {
                RadioStatus.On => new("Bluetooth is on", true, "Bluetooth is turned on.", CheckFix.None, true),
                RadioStatus.Off => new("Bluetooth is on", false, "Bluetooth is turned off.", CheckFix.TurnOnBluetooth, true),
                RadioStatus.Disabled => new("Bluetooth is on", false, "Bluetooth is disabled (airplane mode?). Turn it on in Settings.", CheckFix.OpenBluetoothSettings, true),
                _ => new("Bluetooth is on", false, "Couldn't read the Bluetooth state. Check it in Settings.", CheckFix.OpenBluetoothSettings, true),
            });
        }

        rows.Add(report.HasAudioSource
            ? new(
                "Paired phone",
                true,
                report.AudioSourceCount == 1 ? "1 paired device can play audio on this PC." : $"{report.AudioSourceCount} paired devices can play audio on this PC.",
                CheckFix.None,
                false)
            : new(
                "Paired phone",
                false,
                report.IsRadioOn ? "No paired phone yet. The next step shows how to pair one." : "Turn on Bluetooth first.",
                CheckFix.None,
                false));
        return rows;
    }
}

/// <summary>User-facing onboarding texts (one place, so tests can assert them).</summary>
public static class OnboardingText
{
    public const string PlayNow = "Connected. Play something on your phone now (a video or a song).";

    public const string Success = "It works! You should hear your phone through this PC.";

    public const string KeepSettingsOpen =
        "Keep Settings > Bluetooth & devices open on this PC while you pair: that's when phones can see it.";

    public const string MutedHint = "Phone audio is muted in DualAudio, so it can't be detected. Unmute it to finish.";

    public static string PairInstruction(string pcName) =>
        $"On your phone, open Bluetooth settings, choose \"{pcName}\", and confirm the code on both screens.";

    public static string IPhonePairTip(string pcName) =>
        $"iPhone: Settings > Bluetooth, tap \"{pcName}\" under Other Devices. If it isn't listed, pair from this PC instead: " +
        "Settings > Bluetooth & devices > Add device > Bluetooth, choose your iPhone, and confirm the code on both screens.";

    public static string AndroidPairTip(string pcName) =>
        $"Android: Settings > Connected devices > Pair new device, choose \"{pcName}\", and confirm the code. " +
        "Allow access if the phone asks.";

    public const string NoAudioHelp =
        "No sound from your phone yet. Make sure something is playing and the phone's volume is up.";

    public static string IPhoneNoAudioTip(string pcName) =>
        $"iPhone: open Control Center, tap the audio output button (AirPlay icon) on the music controls, and choose \"{pcName}\".";

    public static string AndroidNoAudioTip(string pcName) =>
        $"Android: in Bluetooth settings, tap the gear next to \"{pcName}\" and make sure Media audio is on.";

    public static string RadioResult(RadioTurnOnResult result) => result switch
    {
        RadioTurnOnResult.AccessDenied => "Windows didn't let DualAudio turn on Bluetooth. Use Open Bluetooth settings instead.",
        RadioTurnOnResult.NoRadio => "No Bluetooth adapter was found to turn on.",
        RadioTurnOnResult.Failed => "Couldn't turn on Bluetooth. Use Open Bluetooth settings instead.",
        _ => string.Empty,
    };
}
