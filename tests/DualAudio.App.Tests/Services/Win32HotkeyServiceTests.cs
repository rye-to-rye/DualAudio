using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using DualAudio.App.Services;
using DualAudio.Core.Abstractions;

namespace DualAudio.App.Tests.Services;

/// <summary>
/// Real RegisterHotKey/UnregisterHotKey on a message-only window (no key presses are simulated, no hooks). Uses
/// Ctrl+Alt+Shift+F24, which no keyboard sends, so it can't collide with anything the user has.
/// </summary>
public sealed class Win32HotkeyServiceTests
{
    private static readonly HotkeyBinding Unusual =
        new(HotkeyAction.ToggleMute, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87); // F24

    [Fact]
    public void A_combination_held_elsewhere_is_refused_and_free_again_after_dispose()
    {
        RunOnDispatcherThread(dispatcher =>
        {
            var first = new Win32HotkeyService(dispatcher);
            var second = new Win32HotkeyService(dispatcher); // stands in for "another app"

            first.TryRegister(Unusual).Should().BeTrue();
            second.TryRegister(Unusual).Should().BeFalse("Windows reports ERROR_HOTKEY_ALREADY_REGISTERED");

            first.Dispose();
            second.TryRegister(Unusual).Should().BeTrue("released on dispose");
            second.Dispose();
        });
    }

    [Fact]
    public void Re_registering_an_action_replaces_its_previous_combination()
    {
        RunOnDispatcherThread(dispatcher =>
        {
            using var sut = new Win32HotkeyService(dispatcher);
            using var probe = new Win32HotkeyService(dispatcher);
            var other = Unusual with { VirtualKey = 0x86 }; // F23

            sut.TryRegister(Unusual).Should().BeTrue();
            sut.TryRegister(other).Should().BeTrue();

            probe.TryRegister(Unusual).Should().BeTrue("the old combination was released");
        });
    }

    private static void RunOnDispatcherThread(Action<Dispatcher> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
