# Phase 0 findings — how received phone audio appears as a Windows audio session

Question: when a phone streams to this PC via `AudioPlaybackConnection`, which WASAPI audio session
carries it, and can we control its volume/mute independently of game audio?

Evidence comes from `tools/DualAudio.Probe` (`sessions`, `snapshot`, `diff`, `setvol`, `mute`, `endpoints`).
Never paste full Bluetooth addresses.

**Summary:** Yes. The phone stream is its own session (process `svchost.exe`, DisplayName
`Microphone (<phone name> A2DP SNK)`), and volume/mute on it change only the phone audio.
Windows keeps one such session per render endpoint; only the one on the current default is Active.

## Test setup

| Field | Value |
|-------|-------|
| Date | 2026-11-11 |
| PC model | TBD |
| Windows build | 26200 |
| Bluetooth adapter model | TBD |
| Bluetooth adapter driver (provider / version / date) | TBD <!-- Device Manager > Bluetooth > adapter > Driver tab --> |
| Default output endpoint (headset) | Headset Earphone on SoundWire Device (8- Cirrus Logic XU (with APO Extensions)) — wired (Scenario B) |
| Other endpoints present | Speakers (Cirrus Logic XU), Speakers (fifine Microphone) |
| Phone model | TBD (Bluetooth name "Shlawg") |
| Phone OS + version | TBD |
| Phone app used for playback | TikTok; PC audio from YouTube in Chrome |

## Results

| Field | Value |
|-------|-------|
| Session appears only after connect + play? (y/n) | Not tested from a clean state (diff before/after not run); session was present whenever connected |
| Endpoint it appears on | Every active render endpoint (one session each); Active only on the current default |
| ProcessID | 3904 (shared svchost; expected to change after reboot) |
| Process name | svchost |
| Same ProcessID on reconnect / after reboot? | Reconnect: yes (3904). Reboot: TBD |
| DisplayName | `Microphone (Shlawg A2DP SNK)` |
| IconPath | `%windir%\system32\mmres.dll,-3014` |
| SessionIdentifier (pattern) | `{endpoint id}\|...\Windows\System32\svchost.exe%b{2EA30F40-6E9F-4709-9327-A4E880941760}` (normal apps have `%b{00000000-...}`) |
| SessionInstanceIdentifier (pattern) | same + `\|0%b3904` (normal apps end `\|8%b<pid>`) |
| IsSystemSoundsSession | False |
| State while playing / paused / after disconnect | Playing: Active on default endpoint. Paused 60 s: link stays Opened. Other endpoints: Inactive |
| **Volume control works — only phone audio changes (y/n)** | **y** (setvol 20/100, confirmed by ear with YouTube playing) |
| **Mute works — only phone audio changes (y/n)** | **y** (confirmed by ear) |
| Volume/mute survive pause/resume on the phone? | TBD |
| Volume/mute survive disconnect + reconnect? | Same session instance id after reconnect, so setvol by instance id still worked; persistence of a non-100 % value across reconnect TBD |
| **Follows default device change (y/n)** | **y** — audio moved to the new default; its session there became Active (Peak 0.127) |
| Session after default-device change (new endpoint? new instance id?) | Separate session per endpoint (instance id differs only in the endpoint-id prefix); each has its own volume |
| Peak meter works on phone session? | y (0.127 while playing; 0.000 readings were quiet moments) |
| Appears in mmsys.cpl Recording tab? | n — no capture-endpoint volume fallback available |
| Measured latency estimate (ms) | TBD |
| Latency method | TBD |

## Identification rule

**Rule:** A phone session is any session, on ANY render endpoint, where the process image is
`svchost.exe` AND DisplayName contains `A2DP SNK`. Secondary check: SessionIdentifier ends with
`%b{2EA30F40-6E9F-4709-9327-A4E880941760}` (pending reboot / second-phone check).
Do not rely on PID (shared svchost, changes on reboot) or on the word "Microphone" (likely localized).

Distinguishes it from other `svchost` / system sessions because: DisplayName names the A2DP sink, and
it is the only non-system session with a non-zero `%b{...}` GUID.

Holds across: [x] reconnect  [ ] probe restart  [ ] PC reboot  [ ] second phone  [x] default-device change

**Design consequences for Prompt 3 / Prompt 5:**
- Apply phone volume/mute to ALL matching sessions across all render endpoints, and re-apply when a new
  matching session appears or the default endpoint changes.
- "Receiving audio" = a matching session on the default endpoint is Active (Peak > 0 confirms sound).
- Phone audio follows the Windows default output; no per-app routing needed for the MVP.

## Connection behaviour findings

- `OpenAsync` returns Success while `State` still reads Closed; `StateChanged -> Opened` follows ~1 ms later.
  Decide "connected" from `StateChanged`, not from reading `State` right after `OpenAsync`.
- PC Bluetooth off mid-stream: Closed -> radio Off -> watcher Removed within ~140 ms (treat as one error).
- Phone Bluetooth off: only `Closed`; device stays in the watcher. Fixed in probe: unexpected Closed after
  Opened = connection lost -> dispose.
- Pausing phone audio for 60 s does NOT fire Closed.
- Idle drop: link closed ~4 min after connect with no radio/watcher change (BUG-002). PC-initiated
  `connect 0` succeeded ~1.5 min later in ~4 s -> auto-reconnect on unexpected Closed is viable.
- Watcher `Updated` events are unreliable for metadata: after the drop it reported `Name="4"` and
  `IsConnected=n/a`. Keep the name from `Added`; don't use IsConnected as a signal.

## Evidence (log excerpts)

```text
sessions (headset default, 13:07:02):
[6] pid=3904 (svchost) State=Active Vol=100% Mute=False SystemSounds=False
    DisplayName="Microphone (Shlawg A2DP SNK)" IconPath="%windir%\system32\mmres.dll,-3014"
    SessionIdentifier={0.0.0.00000000}.{b2498d41-...}|...\svchost.exe%b{2EA30F40-6E9F-4709-9327-A4E880941760}
    SessionInstanceIdentifier=...%b{2EA30F40-6E9F-4709-9327-A4E880941760}|0%b3904

idle drop + reconnect:
13:09:15.810 Connection StateChanged -> Closed
13:09:15.811 Connection lost (state Closed)
13:09:15.813 Watcher Updated: [0] Name="4" ... IsEnabled=True IsConnected=n/a
13:10:42.400 > connect 0
13:10:46.603 OpenAsync: Status=Success
13:10:46.604 Connection StateChanged -> Opened

setvol / mute lines (only phone audio changed by ear):
13:11:23 setvol [6] ... "Microphone (Shlawg A2DP SNK)": 100% -> 20% (read back)
13:11:31 setvol [6] ...: 20% -> 100% (read back)
13:11:39 mute [6] ...: False -> True (read back)
13:11:42 mute [6] ...: True -> False (read back)

sessions all (after switching default to Speakers (Cirrus Logic), 13:20:59):
Speakers (fifine Microphone):            [2] svchost "Microphone (Shlawg A2DP SNK)" State=Inactive Peak=0.000
Speakers (Cirrus Logic) [DEFAULT]:        [6] svchost "Microphone (Shlawg A2DP SNK)" State=Active   Peak=0.127
                                          [7] chrome State=Active Peak=0.140
Headset Earphone [DEFAULT: Communications]: [15] svchost "Microphone (Shlawg A2DP SNK)" State=Inactive Peak=0.000
```

## Still to do

- [ ] Reboot check: GUID after `%b` unchanged? new PID?
- [ ] `diff before after` from a clean (never connected) state
- [ ] Volume set to 20 % survives disconnect + reconnect?
- [ ] Latency measurement
- [ ] Second phone (if available)
- [ ] Fill TBD fields (PC model, adapter + driver, phone model/OS)
