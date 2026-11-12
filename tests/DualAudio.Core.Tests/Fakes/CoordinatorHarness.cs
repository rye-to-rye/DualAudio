using DualAudio.Core.Abstractions;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;

namespace DualAudio.Core.Tests.Fakes;

/// <summary>A coordinator wired to NSubstitute platform fakes and a <see cref="FakeClock"/>.</summary>
internal sealed class CoordinatorHarness : IAsyncDisposable
{
    // Fake address-like ids; never a real device.
    public const string PhoneId = "Bluetooth#Bluetooth00:1a:7d:da:71:13-5c:f3:70:aa:bb:cc";
    public const string OtherPhoneId = "Bluetooth#Bluetooth00:1a:7d:da:71:13-5c:f3:70:11:22:33";
    public const string PhoneName = "Shlawg";

    private readonly List<ConnectionStateChangedEventArgs> _transitions = [];

    public CoordinatorHarness(bool autoReconnect = true, ReconnectOptions? reconnect = null)
    {
        Radio.Status.Returns(RadioStatus.On);
        Watcher.IsEnumerationCompleted.Returns(true);
        Receiver.State.Returns(ReceiverState.Closed);
        Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(Task.FromResult(OpenResult.Ok()));
        Receiver.CloseAsync().Returns(Task.CompletedTask);
        Sessions.GetPhoneAudioStatusAsync(default).ReturnsForAnyArgs(_ => Task.FromResult(PhoneStatus));

        Options = new ConnectionCoordinatorOptions
        {
            AutoReconnect = autoReconnect,
            Reconnect = reconnect ?? new ReconnectOptions(),
            ShutdownSessionCheckTimeout = TimeSpan.Zero, // the FakeClock never advances during DisposeAsync
        };
        Sut = new ConnectionCoordinator(Watcher, Receiver, Radio, Clock, Options, logger: null, Sessions);
        Sut.StateChanged += (_, e) =>
        {
            lock (_transitions)
            {
                _transitions.Add(e);
            }
        };
    }

    public FakeClock Clock { get; } = new();

    public IBluetoothAudioSourceWatcher Watcher { get; } = Substitute.For<IBluetoothAudioSourceWatcher>();

    public IAudioReceiver Receiver { get; } = Substitute.For<IAudioReceiver>();

    public IRadioMonitor Radio { get; } = Substitute.For<IRadioMonitor>();

    public IAudioSessionController Sessions { get; } = Substitute.For<IAudioSessionController>();

    /// <summary>What <see cref="Sessions"/> reports. Default: no phone session (never stale).</summary>
    public PhoneAudioStatus PhoneStatus { get; set; } = PhoneAudioStatus.Unavailable;

    public int StatusReads => Sessions.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAudioSessionController.GetPhoneAudioStatusAsync));

    /// <summary>Sets <see cref="PhoneStatus"/> to one phone session on the default endpoint.</summary>
    public void SetPhoneSession(bool active, float peak = 0f) =>
        PhoneStatus = new PhoneAudioStatus([new PhoneSessionState("{0.0.0.00000000}.{headset}", true, active, 100, false)], peak);

    public ConnectionCoordinatorOptions Options { get; }

    public ConnectionCoordinator Sut { get; }

    public IReadOnlyList<ConnectionStateChangedEventArgs> Transitions
    {
        get { lock (_transitions) { return [.. _transitions]; } }
    }

    public IReadOnlyList<ConnectionState> States => [.. Transitions.Select(t => t.Current)];

    public int OpenCalls => Receiver.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAudioReceiver.OpenAsync));

    public int CloseCalls => Receiver.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAudioReceiver.CloseAsync));

    public void ClearTransitions()
    {
        lock (_transitions)
        {
            _transitions.Clear();
        }
    }

    /// <summary>Waits until every queued transition has run.</summary>
    public Task SettleAsync() => Sut.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

    public async Task AdvanceAsync(TimeSpan by)
    {
        await SettleAsync();
        Clock.Advance(by);
        await SettleAsync();
    }

    public async Task AddDeviceAsync(string id = PhoneId, string name = PhoneName)
    {
        Watcher.DeviceAdded += Raise.EventWith(Watcher, new PhoneDeviceEventArgs(new PhoneDevice(id, name, false)));
        await SettleAsync();
    }

    public async Task UpdateDeviceAsync(string id, string name)
    {
        Watcher.DeviceUpdated += Raise.EventWith(Watcher, new PhoneDeviceEventArgs(new PhoneDevice(id, name, false)));
        await SettleAsync();
    }

    public void RemoveDevice(string id = PhoneId) =>
        Watcher.DeviceRemoved += Raise.EventWith(Watcher, new PhoneDeviceRemovedEventArgs(id));

    public void CompleteEnumeration() => Watcher.EnumerationCompleted += Raise.Event();

    public void RaiseReceiver(ReceiverState state, string id = PhoneId)
    {
        Receiver.State.Returns(state);
        Receiver.StateChanged += Raise.EventWith(Receiver, new ReceiverStateChangedEventArgs(id, state));
    }

    public void RaiseRadio(RadioStatus status)
    {
        Radio.Status.Returns(status);
        Radio.StatusChanged += Raise.EventWith(Radio, new RadioStatusChangedEventArgs(status));
    }

    /// <summary>Drives the coordinator to Connected and clears recorded transitions.</summary>
    public async Task ConnectFullyAsync(string id = PhoneId)
    {
        await AddDeviceAsync(id);
        await Sut.ConnectAsync(id);
        RaiseReceiver(ReceiverState.Opened, id);
        await SettleAsync();
        Sut.CurrentState.Should().Be(ConnectionState.Connected);
        ClearTransitions();
    }

    public void SetOpenResult(OpenResult result) =>
        Receiver.OpenAsync(default!, default).ReturnsForAnyArgs(Task.FromResult(result));

    public void SetOpenFailure(ErrorKind kind) => SetOpenResult(OpenResult.Failed(kind, "0x80070490"));

    public async ValueTask DisposeAsync() => await Sut.DisposeAsync();
}
