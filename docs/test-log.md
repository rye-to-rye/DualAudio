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
| 13 | Connected and playing: turn **the phone's** Bluetooth off (PC Bluetooth stays on). | `Connection StateChanged -> Closed`, then `[WRN] Connection lost (state Closed)` and `Connection to "…" disposed (connection lost).` Note any `Watcher Updated: … System.Devices.Aep.IsConnected=…` lines. No crash. |
| 14 | Turn the phone's Bluetooth back on, then `connect 0` **without** typing `disconnect` first. | Connects (`OpenAsync: Status=Success`); no "Already connected". |
| 15 | Connected and playing: pause audio on the phone for 60 s, then resume. | **No** `Connection lost` line during the pause; audio resumes through the headset. (Confirms pausing doesn't fire Closed.) |

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

## Known issues

### BUG-001: Connected (Status=Success, State=Opened) but no audio from phone

- **When:** 2026-11-11, during Phase 0b (Prompt 2 session testing), on the first `connect` of a new probe run
- **Phone:** "Shlawg" (model / OS: TBD)
- **Fixed by:** `disconnect` then `connect 0`, after which audio played normally
- **Unknown yet:** was the `A2DP SNK` session present? Peak > 0? Was the phone routing audio to the PC?
- **Next time it happens (before resetting):** run `sessions` and note the `A2DP SNK` line (State/Vol/Mute/Peak), and check the phone's audio output picker.
  - No `A2DP SNK` session → PC side (link opened, playback never started)
  - Session present, Peak = 0 → phone side (not sending / routed elsewhere)
  - Session present, Peak > 0, silent → PC output / volume
- **Planned mitigation:** detect "Opened but no audio" via `IsReceivingAudio` (Prompt 5) and offer/attempt reconnect (Prompt 9).
- **Update 2026-11-11 ~20:45-20:57 (now reproducible every time):** on every fresh `coord` start, the phone
  session on the default endpoint is ALREADY Active before we connect (log: `Phone session on default endpoint:
  exists False -> True, active False -> True` at startup, before `connect`). The first OpenAsync succeeds but the
  session never goes Inactive -> Active and no audio arrives. `disconnect` makes it Inactive; the next `connect`
  makes it Active again (fresh) and audio plays within ~3 s.
- **Likely cause:** a stale render pipeline left over from the previous process's connection. Opening on top of
  it doesn't restart the stream; a full close + reopen does.
- **Fix direction:** on connect, if a phone session is already Active while we hold no connection (stale), do a
  priming cycle automatically: open -> close -> short delay -> open. Also make sure quit closes the connection
  before the process exits.

### BUG-002: Audio link drops after a few minutes idle

- **When:** 2026-11-11. Connected 13:05:18; `Connection StateChanged -> Closed` at 13:09:15 (~4 min) while the phone was idle.
- **Phone:** "Shlawg" (model / OS: TBD); screen-lock state at the time: TBD
- **Signals:** only `Closed`. No radio change, no `Watcher Removed`. A `Watcher Updated` followed with `Name="4"`, `IsConnected=n/a`.
- **Recovery:** PC-initiated `connect 0` at 13:10:42 succeeded (`OpenAsync` Success in ~4 s) and audio played; volume/mute tests then worked on the same session.
- **Not caused by pausing:** a 60 s pause did not fire `Closed`.
- **Unknown yet:** which side closed the link (phone power saving vs Windows); exact idle threshold.
- **Planned fix:** ConnectionCoordinator treats unexpected `Closed` as Disconnected and auto-reconnects with backoff (Prompt 3 ReconnectPolicy), so the link is ready when the user picks up the phone.


### BUG-003: Frequent drops under `coord`, then reconnects refused with 0x8007001F

- **When:** 2026-11-11, 15:01 onwards, Prompt 4 `coord` harness (ConnectionCoordinator + real adapters).
- **Phone:** "Shlawg" (model / OS: TBD). AirPods are also paired to this PC; whether they were connected to the phone: TBD.
- **Drops (Connected -> unexpected Closed):** 31 s, 7 s, 13 s, 28 s, 11 s, 50 s, 5 s, 13 s, then one stretch of 8.5 min (15:33:13 -> 15:41:49).
  For comparison, the old probe `connect` (Prompt 1/2 code) held 4-15 min per connection earlier the same day.
- **Reconnect failures:** OpenAsync Status=UnknownFailure, ExtendedError=0x8007001F (ERROR_GEN_FAILURE). Sometimes
  clears after 1-2 retries; after the 15:41:49 drop, every attempt failed (attempts 1-7+ through 15:43:25).
- **Explained (15:41:49 onward):** the phone's Bluetooth was turned off. So with phone BT off, OpenAsync fails with
  UnknownFailure 0x8007001F rather than a distinct error, and the device stays in the watcher. App should map
  0x8007001F to a friendly "Can't reach your phone - is Bluetooth on?" message instead of "Couldn't connect".
- **Recovery so far:** auto-reconnect worked for the short drops (typically 4-6 s gap). Unknown how to clear the persistent-failure state.
- **Unknown yet:** is it our new code (RadioMonitor watcher, name resolver, coordinator timing) or the phone (iPhone moving audio
  to AirPods / power saving)? Needs a controlled A/B: old probe vs `coord`, 5 min each, same conditions, AirPods away.
- **Note:** auto-reconnecting immediately after a phone-initiated close may also be fighting the phone; consider waiting for
  the phone to start playback again, or a longer first backoff, once the cause is known.
