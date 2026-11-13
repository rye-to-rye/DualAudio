using DualAudio.App.Services;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Audio;
using DualAudio.Core.Connection;
using DualAudio.Core.Errors;
using DualAudio.Core.Settings;
using DualAudio.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;

namespace DualAudio.App.Tests.Services;

/// <summary>"Open app -> already connected": what DualAudioRuntime does at start with the saved settings.</summary>
public sealed class DualAudioRuntimeTests
{
    private static readonly AppSettings Saved = AppSettings.Default with { PreferredDeviceId = PhoneId, AutoConnectOnStart = true };

    [Fact]
    public async Task Start_with_a_saved_phone_connects_when_the_watcher_adds_it()
    {
        await using var r = await RuntimeFixture.StartAsync(Saved);
        r.H.Sut.CurrentState.Should().Be(ConnectionState.WaitingForPhone);

        await r.H.AddDeviceAsync();
        r.H.RaiseReceiver(ReceiverState.Opened);
        await r.H.SettleAsync();

        r.H.OpenCalls.Should().Be(1);
        r.H.Sut.CurrentState.Should().Be(ConnectionState.Connected);
        r.H.Watcher.Received(1).Start();
    }

    [Fact]
    public async Task AutoConnectOnStart_off_does_not_connect()
    {
        await using var r = await RuntimeFixture.StartAsync(Saved with { AutoConnectOnStart = false });

        await r.H.AddDeviceAsync();
        await r.H.AdvanceAsync(TimeSpan.FromMinutes(1));

        r.H.OpenCalls.Should().Be(0);
        r.H.Sut.CurrentState.Should().Be(ConnectionState.Idle);
    }

    [Fact]
    public async Task No_saved_phone_does_not_connect()
    {
        await using var r = await RuntimeFixture.StartAsync(AppSettings.Default);

        await r.H.AddDeviceAsync();

        r.H.OpenCalls.Should().Be(0);
    }

    [Fact]
    public async Task Phone_Bluetooth_off_at_start_retries_and_connects_by_itself()
    {
        await using var r = await RuntimeFixture.StartAsync(Saved, addPhoneFirst: true, open: OpenResult.Failed(ErrorKind.PhoneUnreachable, "0x8007001F"));

        r.H.Sut.CurrentState.Should().Be(ConnectionState.Disconnected);
        r.H.Sut.LastError.Should().Be(ErrorKind.PhoneUnreachable);
        r.H.Sut.IsReconnecting.Should().BeTrue();

        r.H.SetOpenResult(OpenResult.Ok());
        await r.H.AdvanceAsync(TimeSpan.FromSeconds(2));
        r.H.RaiseReceiver(ReceiverState.Opened);
        await r.H.SettleAsync();

        r.H.Sut.CurrentState.Should().Be(ConnectionState.Connected);
    }

    [Fact]
    public async Task Manual_disconnect_is_not_undone_and_the_next_start_still_follows_AutoConnectOnStart()
    {
        var store = new InMemorySettingsStore(Saved);
        await using (var first = await RuntimeFixture.StartAsync(store: store, addPhoneFirst: true))
        {
            first.H.RaiseReceiver(ReceiverState.Opened);
            await first.H.SettleAsync();
            await first.H.Sut.DisconnectAsync();
            first.H.RaiseReceiver(ReceiverState.Closed);
            await first.H.AdvanceAsync(TimeSpan.FromMinutes(5));

            first.H.OpenCalls.Should().Be(1, "no reconnect after a manual Disconnect");
        }

        store.Current.PreferredDeviceId.Should().Be(PhoneId, "Disconnect doesn't forget the phone");
        store.Current.AutoConnectOnStart.Should().BeTrue("Disconnect doesn't change the setting");

        await using var restarted = await RuntimeFixture.StartAsync(store: store, addPhoneFirst: true);
        restarted.H.OpenCalls.Should().Be(1, "AutoConnectOnStart is respected on the next start");
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(InMemorySettingsStore store)
        {
            Store = store;
            PhoneAudio = new PhoneAudioService(H.Sessions, H.Sut, Store, H.Clock);
            Runtime = new DualAudioRuntime(H.Sut, PhoneAudio, H.Radio, H.Watcher, Store, NullLogger<DualAudioRuntime>.Instance);
        }

        public CoordinatorHarness H { get; } = new();

        public InMemorySettingsStore Store { get; }

        public PhoneAudioService PhoneAudio { get; }

        public DualAudioRuntime Runtime { get; }

        public static async Task<RuntimeFixture> StartAsync(
            AppSettings? settings = null,
            InMemorySettingsStore? store = null,
            bool addPhoneFirst = false,
            OpenResult? open = null)
        {
            var f = new RuntimeFixture(store ?? new InMemorySettingsStore(settings));
            if (open is not null)
            {
                f.H.SetOpenResult(open);
            }

            if (addPhoneFirst)
            {
                await f.H.AddDeviceAsync();
            }
            else
            {
                f.H.Watcher.IsEnumerationCompleted.Returns(false); // the phone shows up after start
            }

            await f.Runtime.StartAsync();
            await f.H.SettleAsync();
            return f;
        }

        public async ValueTask DisposeAsync()
        {
            await PhoneAudio.DisposeAsync();
            await H.DisposeAsync();
        }
    }
}
