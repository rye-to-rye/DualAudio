# DualAudio architecture

## Projects

| Project | TFM | Role |
|---------|-----|------|
| `src/DualAudio.Core` | `net10.0` | Interfaces, models, connection state machine, reconnect policy, settings model, phone-session rule. No Windows references (enforced by `CoreIsPlatformIndependentTests`). |
| `src/DualAudio.Infrastructure` | `net10.0` | Settings persistence, logging setup (later task). |
| `src/DualAudio.Platform.Windows` | `net10.0-windows10.0.19041.0` | WinRT / CoreAudio / Win32 adapters implementing Core interfaces (later task). |
| `src/DualAudio.App` | `net10.0-windows10.0.19041.0` (WPF) | Tray UI, DI composition root (later task). |
| `tests/DualAudio.Core.Tests` | `net10.0` | xUnit + NSubstitute + FluentAssertions 7.x. |
| `tools/DualAudio.Probe` | `net10.0-windows10.0.19041.0` | Phase 0 hardware probe. |

Dependencies point inward: App → Infrastructure, Platform.Windows → Core.

## Core interfaces (implemented in Platform.Windows / Infrastructure)

| Interface | Wraps |
|-----------|-------|
| `IBluetoothAudioSourceWatcher` | `DeviceWatcher(AudioPlaybackConnection.GetDeviceSelector())` |
| `IAudioReceiver` | `AudioPlaybackConnection` (TryCreateFromId → StartAsync → OpenAsync → Dispose) |
| `IRadioMonitor` | `Windows.Devices.Radios.Radio` (read-only) |
| `IAudioSessionController` | WASAPI sessions matched by `PhoneSessionMatcher`, on all render endpoints (`NAudioSessionController`) |
| `IOutputDeviceService` | Render endpoints + default-endpoint and device-change notifications, read-only (`OutputDeviceService`) |
| `IHotkeyService` | `RegisterHotKey` |
| `ISettingsStore` | JSON file in `%LOCALAPPDATA%` |
| `IClock` | Time / delays (fake in tests) |

## Connection state machine (`ConnectionCoordinator`)

```mermaid
stateDiagram-v2
    [*] --> Idle

    Idle --> Enabling : ConnectAsync(id)
    Disconnected --> Enabling : ConnectAsync(id) / reconnect attempt (backoff elapsed)
    Error --> Enabling : ConnectAsync(id) / radio On and resume pending

    Enabling --> Error : radio Off/Disabled → BluetoothOff\nno radio → NoAdapter\nunknown after enumeration → DeviceNotPaired
    Enabling --> WaitingForPhone : phone not in watcher yet
    Enabling --> Opening : phone known

    WaitingForPhone --> Opening : watcher Added(id)
    WaitingForPhone --> Error : EnumerationCompleted without phone → DeviceNotPaired

    Opening --> Connected : receiver StateChanged → Opened
    Opening --> Error : OpenResult failure (kind)\nOpenTimeout / no Opened → OpenTimedOut\nradio Off → BluetoothOff
    Opening --> Disconnected : retryable failure while auto-reconnecting

    Connected --> Disconnected : unexpected Closed or Removed, settle window over, radio On\n(+ reconnect scheduled if AutoReconnect)
    Connected --> Error : radio Off (alone, or within the Closed/Off/Removed burst) → BluetoothOff

    Disconnected --> Error : reconnect attempts exhausted (last kind)

    Error --> Idle : radio On, nothing to resume

    Enabling --> Disconnected : DisconnectAsync()
    WaitingForPhone --> Disconnected : DisconnectAsync()
    Opening --> Disconnected : DisconnectAsync()
    Connected --> Disconnected : DisconnectAsync()
    Error --> Disconnected : DisconnectAsync()
```

Rules built in from Phase 0 (`docs/phase0-findings.md`, `docs/test-log.md`):

- **Connected is decided by the receiver's `StateChanged → Opened` event**, not by `State` after
  `OpenAsync` (it briefly reads Closed). If Opened never arrives within `OpenedConfirmationTimeout`
  (5 s), the coordinator reads `State` once: Opened → Connected; otherwise Error(OpenTimedOut).
- **Unexpected Closed after Opened = link lost** (BUG-002 idle drop, phone Bluetooth off). After a
  500 ms settle window the coordinator goes to Disconnected and, if `AutoReconnect`, schedules
  `ReconnectPolicy` (2 s, 4 s, 8 s, 16 s, 30 s cap; `MaxAttempts` default 10; success resets).
  Pausing does not fire Closed, so it doesn't trigger this path.
- **PC Bluetooth off** produces Closed + radio Off + Removed within ~140 ms. The settle window plus the
  radio handler collapse the burst into **one** Connected → Error(BluetoothOff) transition. When the radio
  comes back On, the coordinator resumes (WaitingForPhone until the watcher re-adds the phone).
- **Device names** come from watcher `Added` only; `Updated` is ignored (observed `Name="4"`).
  `PhoneDevice.IsConnected` from the watcher is never used as a signal.
- **User Disconnect** acts immediately: bumps an intent version (stale timers/reconnects become no-ops),
  cancels the reconnect policy and any in-flight `OpenAsync`, then queues the transition to Disconnected.

Concurrency: every transition runs inside one `SemaphoreSlim(1)`. Platform events (any thread) and
timer callbacks enqueue work onto it; double-clicked Connect calls queue up and the repeat is ignored.
`StateChanged` is raised on the transitioning thread; the UI must marshal to its dispatcher.

`Error` states and `Disconnected` after a failed reconnect attempt carry an `ErrorKind`; the UI shows
`ErrorMessages.For(kind)` (title + one actionable sentence; no HRESULTs in titles).

## Phone audio session rule (`PhoneSessionMatcher`)

Match on any render endpoint: process image `svchost.exe` **and** DisplayName contains `A2DP SNK`.
The `%b{2EA30F40-6E9F-4709-9327-A4E880941760}` session GUID is reported as extra confirmation only
(`PhoneSessionMatch.PrimaryAndGuid`) and never matches on its own. Never match on PID or "Microphone".

## Phone volume / mute and "receiving audio"

**Platform: `NAudioSessionController`** (`src/DualAudio.Platform.Windows/Audio/`). This class does all of its COM work on one dedicated MTA thread
(`MtaWorker`). Core Audio callbacks (session created, default endpoint changed, endpoint added/removed/state,
session state/expired) only post a rescan to that thread. A rescan enumerates the sessions on every active render
endpoint, keeps the phone sessions, and writes the requested volume/mute to each one whose value differs. It never
writes to a non-phone session. The requested values are remembered, so a session that appears later (reconnect,
new default, new endpoint) gets them too. `PhoneSessionsChanged` fires when the phone session on the default endpoint
appears, disappears, or changes between Active and Inactive. `GetPhoneAudioStatusAsync` returns every phone
session's state plus the default session's peak. When no session exists, it returns `Available = false`.
A change made in the Windows volume mixer is overwritten by the next re-apply.

**Core: `PhoneAudioService`**
- Loads `PhoneVolume`/`PhoneMuted` from `AppSettings` and applies them. Nothing is applied before `InitializeAsync`.
- Persists every change. It re-reads the settings first, so other fields are kept.
- Re-applies the saved values on every transition to Connected.
- While Connected, and only then, it polls the status every 250 ms (the interval is clamped to at least 250 ms).
- `IsReceivingAudio` is true when the default endpoint's phone session is Active and its peak was above 0.001
  at least once in the last 3 s. It is separate from Connected: a paused phone stays Connected but stops receiving.
- `Changed` follows the coordinator's rules: decided under one lock, raised after release, in order.

`DefaultEndpointMasterVolume` is optional and controls the whole device. It changes the default output's master volume
(game audio too) and is not used for phone volume.
