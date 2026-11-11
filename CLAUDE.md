
# DualAudio — project context for AI assistants



What it is: a Windows 11 tray app that lets a paired phone stream media audio to the PC over

Bluetooth (A2DP sink) so it plays through the user's existing PC headset, mixed with game audio.



Hard facts (do not contradict without evidence):

- The ONLY receive mechanism is Windows.Media.Audio.AudioPlaybackConnection (Win10 2004 / build 19041+).

  Flow: DeviceWatcher(AudioPlaybackConnection.GetDeviceSelector()) -> TryCreateFromId(id) ->

  StartAsync() -> OpenAsync() (check AudioPlaybackConnectionOpenResult.Status) -> Dispose() to release.

  Audio stops when the connection object is disposed or the process exits.

- Windows renders and mixes the received audio itself. We never touch raw samples.

- Pairing is done by the user in Windows Settings (ms-settings:bluetooth). Never pair programmatically.

- No admin rights, no drivers, no undocumented APIs (no IAudioPolicyConfig), no low-level keyboard hooks.

- Phone calls / microphone forwarding are OUT OF SCOPE.



Stack: C# / .NET 10, WPF + WPF-UI, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, NAudio.Wasapi,

Microsoft.Extensions.Hosting/Logging + Serilog, xUnit + NSubstitute + FluentAssertions, CsWin32 for Win32.

TFM for Windows projects: net10.0-windows10.0.19041.0. Core project: plain net10.0 (no Windows refs).



Solution layout: src/DualAudio.App (WPF), src/DualAudio.Core (logic, interfaces, state machine),

src/DualAudio.Platform.Windows (WinRT/CoreAudio/Win32 adapters), src/DualAudio.Infrastructure

(settings, logging), tests/DualAudio.Core.Tests, tests/DualAudio.Platform.Tests, tools/DualAudio.Probe.



Rules for every change:

- Keep Core free of Windows types. Platform adapters implement Core interfaces.

- All Bluetooth/audio calls are async, cancellable, never on the UI thread, never throw into the UI:

  map failures to Core ErrorKind values with user-friendly messages.

- Dispose COM/WinRT objects deterministically. No static mutable state.

- Never log audio content or full Bluetooth addresses (log last 4 hex chars only).

- Make only the changes the current task asks for. If something requested looks unsound, say so first.

- After each task: list files changed, how to build, how to verify on real hardware, known limitations.


