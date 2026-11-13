using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.Core.Hotkeys;

public enum HotkeyChangeResult
{
    Changed,
    Unchanged,

    /// <summary>RegisterHotKey refused it: another app owns the combination. The previous binding is kept.</summary>
    InUseByAnotherApp,

    /// <summary>Another DualAudio shortcut already uses the combination.</summary>
    AlreadyUsedInDualAudio,

    /// <summary>Not allowed (see <see cref="HotkeyFormat.IsAllowed"/>).</summary>
    Invalid,
}

/// <param name="IsRegistered">False when Windows refused it at start-up (combination owned by another app).</param>
public sealed record HotkeyState(HotkeyBinding Binding, bool IsRegistered);

/// <summary>
/// Owns DualAudio's global shortcuts: registers the saved bindings, changes one with fallback to the previous binding
/// if Windows refuses the new one, and runs the actions (phone mute, phone volume ±5 %). Hotkey feedback is the tray
/// tooltip only (via <see cref="PhoneAudioService.Changed"/>); nothing here opens a window.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const int VolumeStep = 5;

    /// <summary>The shortcuts DualAudio offers.</summary>
    public static IReadOnlyList<HotkeyAction> ManagedActions { get; } = [HotkeyAction.ToggleMute, HotkeyAction.VolumeUp, HotkeyAction.VolumeDown];

    private readonly IHotkeyService _hotkeys;
    private readonly PhoneAudioService _phoneAudio;
    private readonly ISettingsStore _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<HotkeyAction, HotkeyState> _states = [];
    private bool _disposed;

    public HotkeyManager(IHotkeyService hotkeys, PhoneAudioService phoneAudio, ISettingsStore settings, ILogger<HotkeyManager>? logger = null)
    {
        _hotkeys = hotkeys;
        _phoneAudio = phoneAudio;
        _settings = settings;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>Registrations changed (applied, changed, reset). Raised on the calling thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyDictionary<HotkeyAction, HotkeyState> Current
    {
        get { lock (_sync) { return new Dictionary<HotkeyAction, HotkeyState>(_states); } }
    }

    /// <summary>Test hook: the latest action started by a hotkey.</summary>
    internal Task LastAction { get; private set; } = Task.CompletedTask;

    /// <summary>(Re-)registers every managed shortcut from the saved settings. Refused ones stay unregistered and are logged.</summary>
    public async Task ApplyFromSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _hotkeys.UnregisterAll();
            var states = new Dictionary<HotkeyAction, HotkeyState>();
            foreach (var action in ManagedActions)
            {
                var binding = settings.HotkeyFor(action) ?? AppSettings.DefaultHotkeys.First(h => h.Action == action);
                var registered = HotkeyFormat.IsAllowed(binding) && !states.Values.Any(s => s.IsRegistered && HotkeyFormat.SameCombination(s.Binding, binding))
                                 && _hotkeys.TryRegister(binding);
                states[action] = new HotkeyState(binding, registered);
                if (registered)
                {
                    _logger.LogInformation("Hotkey {Action} = {Keys}", action, HotkeyFormat.Format(binding));
                }
                else
                {
                    _logger.LogWarning("Hotkey {Action} = {Keys} could not be registered (in use by another app?)", action, HotkeyFormat.Format(binding));
                }
            }

            lock (_sync)
            {
                _states = states;
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Changes one shortcut. If Windows refuses the new combination, the previous binding is registered again and kept.
    /// Saved to settings only on success.
    /// </summary>
    public async Task<HotkeyChangeResult> ChangeAsync(HotkeyBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!HotkeyFormat.IsAllowed(binding) || !ManagedActions.Contains(binding.Action))
        {
            return HotkeyChangeResult.Invalid;
        }

        HotkeyChangeResult result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HotkeyState? previous;
            bool duplicate;
            lock (_sync)
            {
                _states.TryGetValue(binding.Action, out previous);
                duplicate = _states.Values.Any(s => s.Binding.Action != binding.Action && HotkeyFormat.SameCombination(s.Binding, binding));
            }

            if (previous is { IsRegistered: true } && HotkeyFormat.SameCombination(previous.Binding, binding))
            {
                return HotkeyChangeResult.Unchanged;
            }

            if (duplicate)
            {
                return HotkeyChangeResult.AlreadyUsedInDualAudio;
            }

            if (previous is { IsRegistered: true })
            {
                _hotkeys.Unregister(binding.Action);
            }

            if (_hotkeys.TryRegister(binding))
            {
                SetState(new HotkeyState(binding, true));
                var current = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
                await _settings.SaveAsync(current.WithHotkey(binding), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Hotkey {Action} changed to {Keys}", binding.Action, HotkeyFormat.Format(binding));
                result = HotkeyChangeResult.Changed;
            }
            else
            {
                _logger.LogWarning("Hotkey {Keys} is in use by another app; keeping the previous binding", HotkeyFormat.Format(binding));
                if (previous is { IsRegistered: true } && !_hotkeys.TryRegister(previous.Binding))
                {
                    _logger.LogWarning("Re-registering the previous hotkey {Keys} failed", HotkeyFormat.Format(previous.Binding));
                    SetState(previous with { IsRegistered = false });
                }

                return HotkeyChangeResult.InUseByAnotherApp;
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkeys.HotkeyPressed -= OnHotkeyPressed;
        try
        {
            _hotkeys.UnregisterAll();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unregistering hotkeys failed");
        }
    }

    private void SetState(HotkeyState state)
    {
        lock (_sync)
        {
            _states = new Dictionary<HotkeyAction, HotkeyState>(_states) { [state.Binding.Action] = state };
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        // Runs on the hotkey window's (UI) thread: hand the audio work to the pool right away.
        LastAction = Task.Run(() => RunActionAsync(e.Action));
    }

    private async Task RunActionAsync(HotkeyAction action)
    {
        try
        {
            switch (action)
            {
                case HotkeyAction.ToggleMute:
                    await _phoneAudio.SetMutedAsync(!_phoneAudio.Muted).ConfigureAwait(false);
                    break;
                case HotkeyAction.VolumeUp:
                    await _phoneAudio.SetVolumeAsync(Math.Min(100, _phoneAudio.VolumePercent + VolumeStep)).ConfigureAwait(false);
                    break;
                case HotkeyAction.VolumeDown:
                    await _phoneAudio.SetVolumeAsync(Math.Max(0, _phoneAudio.VolumePercent - VolumeStep)).ConfigureAwait(false);
                    break;
            }

            _logger.LogDebug("Hotkey {Action}: volume {Volume}%, muted {Muted}", action, _phoneAudio.VolumePercent, _phoneAudio.Muted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hotkey action {Action} failed", action);
        }
    }
}
