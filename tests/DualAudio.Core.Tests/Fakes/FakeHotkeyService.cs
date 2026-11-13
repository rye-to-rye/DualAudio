using DualAudio.Core.Abstractions;
using DualAudio.Core.Hotkeys;

namespace DualAudio.Core.Tests.Fakes;

/// <summary>Fake registrar: combinations in <see cref="TakenByOtherApps"/> are refused, like RegisterHotKey would.</summary>
internal sealed class FakeHotkeyService : IHotkeyService
{
    private readonly Dictionary<HotkeyAction, HotkeyBinding> _registered = [];

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public List<HotkeyBinding> TakenByOtherApps { get; } = [];

    public IReadOnlyDictionary<HotkeyAction, HotkeyBinding> Registered => _registered;

    public int RegisterCalls { get; private set; }

    public bool TryRegister(HotkeyBinding binding)
    {
        RegisterCalls++;
        if (TakenByOtherApps.Any(t => HotkeyFormat.SameCombination(t, binding))
            || _registered.Values.Any(r => r.Action != binding.Action && HotkeyFormat.SameCombination(r, binding)))
        {
            return false;
        }

        _registered[binding.Action] = binding;
        return true;
    }

    public void Unregister(HotkeyAction action) => _registered.Remove(action);

    public void UnregisterAll() => _registered.Clear();

    /// <summary>Simulates WM_HOTKEY for a registered combination.</summary>
    public void Press(HotkeyAction action)
    {
        if (_registered.ContainsKey(action))
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(action));
        }
    }

    public void Dispose() => _registered.Clear();
}

internal sealed class FakeAutoStartEntry : Core.Startup.IAutoStartEntry
{
    public string? Value { get; set; }

    public bool Throw { get; set; }

    public int Writes { get; private set; }

    public string? Read() => Throw ? throw new UnauthorizedAccessException("denied") : Value;

    public void Write(string command)
    {
        if (Throw)
        {
            throw new UnauthorizedAccessException("denied");
        }

        Writes++;
        Value = command;
    }

    public void Remove() => Value = Throw ? throw new UnauthorizedAccessException("denied") : null;
}
