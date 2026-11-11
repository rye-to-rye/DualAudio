# DualAudio.Probe — hardware test log

Goal: prove this PC can receive Bluetooth A2DP audio from an already-paired phone via
`Windows.Media.Audio.AudioPlaybackConnection`.

## How to run

```powershell
cd C:\dev\DualAudio
dotnet build DualAudio.sln -c Debug
dotnet run --project tools\DualAudio.Probe\DualAudio.Probe.csproj
```

The log is appended to `tools\DualAudio.Probe\bin\Debug\net10.0-windows10.0.19041.0\probe-log.txt`.
After each run, paste the relevant lines into the results below.

## Manual test script

| # | Step | Expected |
|---|------|----------|
| 0 | Before starting: Settings > Bluetooth & devices > turn Bluetooth **On**, pair the phone (if it isn't already). Plug in or connect the PC headset and make it the default output. | Phone shows as "Paired" in Settings. |
| 1 | Start the probe. | OS line shows build ≥ 19041, "AudioPlaybackConnection is present", "Bluetooth radio … is On", `EnumerationCompleted: N device(s)` with N ≥ 1. |
| 2 | `list` | The phone appears, e.g. `[0] Name="My Phone" … IsEnabled=True`. Note its index. |
| 3 | `connect 0` (use the phone's index) | `StartAsync completed`, then `OpenAsync: Status=Success, ExtendedError=none, State=Opened`. A `StateChanged -> Opened` line may also appear. |
| 4 | On the phone, play a TikTok/YouTube video. On an iPhone, open the AirPlay/audio output picker and choose the PC if audio stays on the phone. | Audio plays through the PC headset. Phone speaker is silent. |
| 5 | Start a game (or any PC audio) on the PC while the phone keeps playing. | You hear both through the headset. Each is also listed in Settings > System > Sound > Volume mixer. |
| 6 | `state` | `Connection: "My Phone" … State=Opened`, Watcher `EnumerationCompleted`, radio `On`. |
| 7 | `disconnect` | `Connection to "…" disposed (user disconnect).` Phone audio returns to the phone speaker within a few seconds. |
| 8 | `connect 0` again (no restart) | `Status=Success` again, and audio comes back to the PC. |
| 9 | While audio is playing, turn Bluetooth **off** (Action Center or Settings). | Probe logs `Bluetooth radio … state changed -> Off`, then `Connection StateChanged -> Closed` and/or `Watcher Removed` followed by `connected device … was removed - disposing connection`. **No crash**; the prompt still accepts commands. |
| 10 | Turn Bluetooth back on, wait for `Watcher Added`, then `list` and `connect <index>`. | Connects again. |
| 11 | `quit` | `Shutting down …` then `Shutdown complete.` and the process exits with code 0. |
| 12 | (Optional) Repeat with Ctrl+C instead of `quit`. | Same clean shutdown. |

Optional negative checks:
- `connect 99` gives `No device at index 99`.
- Start the probe with Bluetooth off: the probe warns that the radio is Off and keeps running.
- Turn the phone's Bluetooth off during `connect`: you see `OpenAsync cancelled: device removed` or a non-Success Status with an HRESULT. No crash.

## Results

| Date | PC (model / BT adapter) | Windows build | Phone (model / OS) | Steps passed | Notes / HRESULTs |
|------|-------------------------|---------------|--------------------|--------------|------------------|
| 2026-11-11 | Dev PC (adapter: ?) | 26200 | — | Startup only | Smoke run, no phone: OS check OK, AudioPlaybackConnection present, BT radio reported **Off**, watcher found 0 devices, `list`/`state`/`quit` worked, exit code 0. Hardware steps 1–12 **not yet run**. |
| | | | | | |

**Acceptance:** steps 1–11 pass on at least one PC. **Status: NOT YET ACCEPTED.**
