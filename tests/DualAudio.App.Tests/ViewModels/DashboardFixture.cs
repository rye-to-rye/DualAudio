using DualAudio.App.Services;
using DualAudio.App.ViewModels;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Settings;
using DualAudio.Core.Tests.Fakes;

namespace DualAudio.App.Tests.ViewModels;

/// <summary>A real ConnectionCoordinator + PhoneAudioService on NSubstitute platform fakes, driving the ViewModel.</summary>
internal sealed class DashboardFixture : IAsyncDisposable
{
    public DashboardFixture(AppSettings? settings = null, bool queueUiWork = false)
    {
        Store = new InMemorySettingsStore(settings);
        PhoneAudio = new PhoneAudioService(H.Sessions, H.Sut, Store, H.Clock);
        Outputs.GetDefaultEndpointAsync(default)
            .ReturnsForAnyArgs(Task.FromResult<AudioEndpoint?>(new AudioEndpoint("{0.0.0.00000000}.{headset}", "Headset Earphone", true)));
        Dispatcher = new TestDispatcher { QueueWork = queueUiWork };
        Vm = new DashboardViewModel(H.Sut, PhoneAudio, H.Watcher, Outputs, Store, Launcher, Dispatcher);
    }

    public CoordinatorHarness H { get; } = new();

    public InMemorySettingsStore Store { get; }

    public PhoneAudioService PhoneAudio { get; }

    public IOutputDeviceService Outputs { get; } = Substitute.For<IOutputDeviceService>();

    public ISystemSettingsLauncher Launcher { get; } = Substitute.For<ISystemSettingsLauncher>();

    public TestDispatcher Dispatcher { get; }

    public DashboardViewModel Vm { get; }

    public static async Task<DashboardFixture> CreateAsync(AppSettings? settings = null, bool withPhone = true)
    {
        var f = new DashboardFixture(settings);
        await f.PhoneAudio.InitializeAsync();
        if (withPhone)
        {
            await f.H.AddDeviceAsync();
        }

        await f.Vm.InitializeAsync();
        await f.SettleAsync();
        return f;
    }

    /// <summary>Waits for the coordinator, phone audio and any VM pushes to finish.</summary>
    public async Task SettleAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Vm.VolumePush.WaitAsync(TimeSpan.FromSeconds(10));
            await Vm.MutePush.WaitAsync(TimeSpan.FromSeconds(10));
            await H.SettleAsync();
            await PhoneAudio.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Advances the fake clock (e.g. a 250 ms peak poll) and settles.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        await SettleAsync();
        H.Clock.Advance(by);
        await SettleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Vm.Dispose();
        await PhoneAudio.DisposeAsync();
        await H.DisposeAsync();
    }
}

/// <summary>Runs posted work inline (serialized), or queues it until <see cref="Pump"/> when <see cref="QueueWork"/> is set.</summary>
internal sealed class TestDispatcher : IUiDispatcher
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();

    public bool QueueWork { get; set; }

    public int Pending
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    public void Post(Action action)
    {
        lock (_gate)
        {
            if (QueueWork)
            {
                _queue.Enqueue(action);
                return;
            }

            action();
        }
    }

    public void Pump()
    {
        lock (_gate)
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
