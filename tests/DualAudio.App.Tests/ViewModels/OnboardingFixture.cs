using DualAudio.App.Services;
using DualAudio.App.ViewModels;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Settings;
using DualAudio.Core.Tests.Fakes;

namespace DualAudio.App.Tests.ViewModels;

/// <summary>A real coordinator + PhoneAudioService on platform fakes, driving the onboarding ViewModel.</summary>
internal sealed class OnboardingFixture : IAsyncDisposable
{
    public const string PcName = "GAMING-PC";

    public static readonly CapabilityReport AllGood = new(26200, true, RadioStatus.On, 1, true);

    public OnboardingFixture(AppSettings? settings = null)
    {
        Store = new InMemorySettingsStore(settings);
        PhoneAudio = new PhoneAudioService(H.Sessions, H.Sut, Store, H.Clock);
        Capabilities.CheckAsync(default).ReturnsForAnyArgs(_ => Task.FromResult(Report));
        Capabilities.GetPcBluetoothNameAsync(default).ReturnsForAnyArgs(Task.FromResult(PcName));
        RadioControl.TurnOnAsync(default).ReturnsForAnyArgs(Task.FromResult(RadioTurnOnResult.TurnedOn));
        Vm = new OnboardingViewModel(
            H.Sut, PhoneAudio, H.Watcher, H.Radio, Capabilities, RadioControl, Store, Launcher, new TestDispatcher(), H.Clock);
    }

    public CoordinatorHarness H { get; } = new();

    public InMemorySettingsStore Store { get; }

    public PhoneAudioService PhoneAudio { get; }

    public ICapabilityService Capabilities { get; } = Substitute.For<ICapabilityService>();

    public IBluetoothRadioControl RadioControl { get; } = Substitute.For<IBluetoothRadioControl>();

    public ISystemSettingsLauncher Launcher { get; } = Substitute.For<ISystemSettingsLauncher>();

    /// <summary>What the capability service reports.</summary>
    public CapabilityReport Report { get; set; } = AllGood;

    public OnboardingViewModel Vm { get; }

    public static async Task<OnboardingFixture> CreateAsync(AppSettings? settings = null, CapabilityReport? report = null, bool withPhone = true)
    {
        var f = new OnboardingFixture(settings);
        if (report is not null)
        {
            f.Report = report;
        }

        await f.PhoneAudio.InitializeAsync();
        if (withPhone)
        {
            await f.H.AddDeviceAsync();
        }

        await f.Vm.InitializeAsync();
        await f.SettleAsync();
        return f;
    }

    /// <summary>Checks -> Pair -> Pick (phone selected) -> Connect.</summary>
    public async Task GoToConnectStepAsync(string? nickname = null)
    {
        await Vm.NextCommand.ExecuteAsync(null); // -> Pair
        await Vm.NextCommand.ExecuteAsync(null); // -> Pick
        Vm.SelectedDevice.Should().NotBeNull();
        Vm.Nickname = nickname ?? string.Empty;
        await Vm.NextCommand.ExecuteAsync(null); // -> Connect
        await SettleAsync();
    }

    public async Task SettleAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Vm.CheckTask.WaitAsync(TimeSpan.FromSeconds(10));
            await Vm.ConnectTask.WaitAsync(TimeSpan.FromSeconds(10));
            await Vm.SaveTask.WaitAsync(TimeSpan.FromSeconds(10));
            await H.SettleAsync();
            await PhoneAudio.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

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
