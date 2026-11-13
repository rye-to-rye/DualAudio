using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DualAudio.App.Tests.ViewModels;
using DualAudio.App.Views;
using DualAudio.Core.Abstractions;
using DualAudio.Core.Errors;
using static DualAudio.Core.Tests.Fakes.CoordinatorHarness;
using AppVm = DualAudio.App.ViewModels;

namespace DualAudio.App.Tests.Views;

/// <summary>
/// Loads every window and both theme dictionaries on an STA thread and lays them out (which also instantiates data
/// templates), so markup errors fail here, not at start-up. WPF-UI's dictionaries are added underneath, as the app
/// does. Also checks the dashboard's per-state texts (MinDashboard renderVals) and that nothing uses an Effect.
/// </summary>
public sealed class WindowXamlTests
{
    private static readonly Size WindowSize = new(960, 640);

    // ------------------------------------------------------------------ resource dictionaries

    [Fact]
    public void Tokens_dictionary_loads_with_frozen_brushes_for_every_colour()
    {
        RunSta(() =>
        {
            var tokens = LoadDictionary("Themes/Tokens.xaml");

            foreach (var key in new[] { "BackgroundBrush", "RaisedBrush", "Raised2Brush", "LineBrush", "TextBrush", "TextMutedBrush", "AccentBrush", "OnAccentBrush", "WarningBrush" })
            {
                tokens[key].Should().BeOfType<SolidColorBrush>(key).Which.IsFrozen.Should().BeTrue(key);
            }

            ((SolidColorBrush)tokens["AccentBrush"]).Color.Should().Be(Color.FromRgb(0x7C, 0x9C, 0xFF));
            ((SolidColorBrush)tokens["LineBrush"]).Color.Should().Be(Color.FromRgb(0x2A, 0x2F, 0x38));
            tokens["CardRadius"].Should().Be(new CornerRadius(12));
            tokens["ControlRadius"].Should().Be(new CornerRadius(10));
            tokens["WindowPadding"].Should().Be(new Thickness(24));
        });
    }

    [Fact]
    public void Controls_dictionary_loads_with_every_shared_style()
    {
        RunSta(() =>
        {
            var controls = LoadDictionary("Themes/Controls.xaml");

            foreach (var key in new object[]
                     {
                         "Card", "PrimaryButton", "LinkButton", "IconButton", "KeyButton", "StatusPill", "StatusDot", "KbdChip", "CardHeading",
                         "LevelMeter", "MuteToggle", "PrimaryMuteToggle", "SettingRowCheckBox", "RadioCardList", "FocusOutline", "TrayMenu",
                         "MonogramImage", typeof(Button), typeof(Slider), typeof(CheckBox), typeof(TextBox), typeof(ComboBox), typeof(ScrollBar),
                     })
            {
                controls.Contains(key).Should().BeTrue(key.ToString());
            }

            // The level meter style works (it isn't placed in a window yet: no view model exposes the level).
            var meter = new ProgressBar { Style = (Style)controls["LevelMeter"], Value = 0.5, Width = 200 };
            Lay(meter, new Size(200, 10));
        });
    }

    // ------------------------------------------------------------------ dashboard

    [Fact]
    public async Task Main_window_xaml_loads()
    {
        await using var f = await DashboardFixture.CreateAsync();
        RunSta(() => LoadWindow(new MainWindow(f.Vm, Shell())));
    }

    [Fact]
    public async Task Dashboard_state_connected()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        await f.SettleAsync();

        var texts = DashboardTexts(f);

        texts.Should().Contain(["Connected", "Connected · nothing playing on the phone", "Disconnect", PhoneName]);
        texts.Should().NotContain(["Connect", "Retry"]);
    }

    [Fact]
    public async Task Dashboard_state_receiving()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true, peak: 0.3f);
        await f.AdvanceAsync(TimeSpan.FromMilliseconds(250));

        DashboardTexts(f).Should().Contain(["Receiving audio", "Connected · receiving audio", "Disconnect", "Mute phone", "Game audio is never changed"]);
    }

    [Fact]
    public async Task Dashboard_state_reconnecting()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.SetOpenResult(OpenResult.Failed(ErrorKind.PhoneUnreachable, "0x8007001F"));
        f.H.RaiseReceiver(ReceiverState.Closed);
        await f.AdvanceAsync(TimeSpan.FromMilliseconds(500));
        await f.AdvanceAsync(TimeSpan.FromSeconds(2)); // first retry fails -> banner

        var texts = DashboardTexts(f);

        texts.Should().Contain(["Reconnecting…", "Lost the link, trying again", "Cancel", "Can't reach your phone", "Retry now"]);
        texts.Should().NotContain("Disconnect");
    }

    [Fact]
    public async Task Dashboard_state_not_found()
    {
        await using var f = await DashboardFixture.CreateAsync(withPhone: false);
        await f.H.Sut.AutoConnectAsync(PhoneId); // enumeration complete without the phone -> calm "gave up"
        await f.SettleAsync();

        var texts = DashboardTexts(f);

        texts.Should().Contain(["Phone not found", "Saved phone · not reachable", "Retry"]);
        texts.Should().NotContain(["Connect", "Disconnect", "Can't reach your phone"]);
    }

    [Fact]
    public async Task Dashboard_state_bluetooth_off()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.RaiseRadio(RadioStatus.Off);
        await f.SettleAsync();

        var texts = DashboardTexts(f);

        texts.Should().Contain(["Bluetooth is off", "Saved phone", "Open Bluetooth settings"]);
        texts.Should().NotContain(["Connect", "Disconnect", "Error"]);
    }

    [Fact]
    public async Task Dashboard_muted_shows_Unmute()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true);
        await f.PhoneAudio.SetMutedAsync(true);
        await f.SettleAsync();

        DashboardTexts(f).Should().Contain("Unmute phone").And.NotContain("Mute phone");
    }

    // ------------------------------------------------------------------ onboarding

    [Theory]
    [InlineData(AppVm.OnboardingStep.Checks, new[] { "Check this PC", "Windows version", "Check again", "Next", "Back" })]
    [InlineData(AppVm.OnboardingStep.Pair, new[] { "Pair your phone", "This PC is called", "GAMING-PC", "iPhone", "Android", "Next" })]
    [InlineData(AppVm.OnboardingStep.PickPhone, new[] { "Pick your phone", PhoneName, "Paired · ready", "Connect", "Back" })]
    [InlineData(AppVm.OnboardingStep.Connect, new[] { "Play something on your phone", "Waiting…", "Back" })]
    public async Task Onboarding_window_xaml_loads_on_every_step(AppVm.OnboardingStep step, string[] expected)
    {
        // A fresh view model per step: a window binds its collections to its own (test) thread.
        await using var f = await OnboardingFixture.CreateAsync(); // check rows present, so the item template is realized
        while (f.Vm.Step != step)
        {
            await f.Vm.NextCommand.ExecuteAsync(null);
            await f.SettleAsync();
        }

        var texts = RunSta(() => VisibleTexts(new OnboardingWindow(f.Vm)));

        texts.Should().Contain(expected);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public async Task Settings_window_xaml_loads_and_shows_a_conflict()
    {
        await using var f = await DashboardFixture.CreateAsync();
        var registrar = new Core.Tests.Fakes.FakeHotkeyService();
        registrar.TakenByOtherApps.Add(Core.Settings.AppSettings.DefaultHotkeys[2]);
        using var hotkeys = new Core.Hotkeys.HotkeyManager(registrar, f.PhoneAudio, f.Store);
        await hotkeys.ApplyFromSettingsAsync();
        var vm = new AppVm.SettingsViewModel(
            f.Store,
            hotkeys,
            new Core.Startup.AutoStartRegistration(new Core.Tests.Fakes.FakeAutoStartEntry()),
            f.H.Sut,
            f.PhoneAudio,
            f.Launcher,
            Substitute.For<global::DualAudio.App.Services.IConfirmDialog>(),
            f.Dispatcher,
            new global::DualAudio.Infrastructure.AppPaths(Path.GetTempPath()));
        await vm.InitializeAsync();

        var texts = RunSta(() => VisibleTexts(new SettingsWindow(vm)));

        texts.Should().Contain(["General", "Shortcuts", "About", "Ctrl+Alt+M", "Ctrl+Alt+PgDn", "Shortcut in use by another app", "Reset settings…"]);
    }

    // ------------------------------------------------------------------ tray

    [Fact]
    public async Task Tray_icon_flyout_and_menu_xaml_load_without_creating_an_icon()
    {
        await using var f = await DashboardFixture.CreateAsync();
        RunSta(() =>
        {
            using var tray = new TrayIcon(new AppVm.TrayViewModel(f.Vm, Shell()));
            tray.IsCreated.Should().BeFalse("nothing appears in the notification area until Show()");
            var flyout = tray.TrayPopup.Should().BeAssignableTo<FrameworkElement>().Subject;
            flyout.DataContext = tray.DataContext;
            Lay(flyout, new Size(360, 460));
            AssertNoEffects(flyout);
            Texts(flyout).Should().Contain(["DualAudio", "Phone volume", "Mute phone", "Open DualAudio"]);
            tray.ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header as string)
                .Should().Equal("Connect", "Disconnect", "Mute phone audio", "Open DualAudio", "Settings", "Exit");
        });
    }

    [Fact]
    public async Task Tray_icon_switches_to_the_muted_icon_while_phone_audio_is_muted()
    {
        await using var f = await DashboardFixture.CreateAsync();
        await f.H.ConnectFullyAsync();
        f.H.SetPhoneSession(active: true);
        await f.SettleAsync();

        string? IconFile()
        {
            string? file = null;
            RunSta(() =>
            {
                using var tray = new TrayIcon(new AppVm.TrayViewModel(f.Vm, Shell()));
                file = (tray.IconSource as BitmapFrame)?.Decoder?.ToString() ?? tray.IconSource?.ToString();
            });
            return file;
        }

        IconFile().Should().EndWith("Assets/DualAudio.ico");

        await f.PhoneAudio.SetMutedAsync(true);
        await f.SettleAsync();

        IconFile().Should().EndWith("Assets/DualAudio-muted.ico");
    }

    // ------------------------------------------------------------------ helpers

    private static AppVm.ShellViewModel Shell() =>
        new(new Core.Tests.Fakes.InMemorySettingsStore(), Substitute.For<AppVm.IAppShell>());

    private static IReadOnlyList<string> DashboardTexts(DashboardFixture f) => RunSta(() => VisibleTexts(new MainWindow(f.Vm, Shell())));

    private static ResourceDictionary LoadDictionary(string path) =>
        new() { Source = new Uri($"pack://application:,,,/DualAudio;component/{path}", UriKind.Absolute) };

    /// <summary>Adds WPF-UI's dictionaries UNDER the window's own (as at runtime, where they are app-level), then lays out.</summary>
    private static void LoadWindow(Window window)
    {
        window.Resources.MergedDictionaries.Insert(0, new Wpf.Ui.Markup.ControlsDictionary());
        window.Resources.MergedDictionaries.Insert(0, new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        var content = (FrameworkElement)window.Content;
        Lay(content, WindowSize);
        AssertNoEffects(content);
    }

    private static IReadOnlyList<string> VisibleTexts(Window window)
    {
        LoadWindow(window);
        var texts = Texts((FrameworkElement)window.Content);
        window.Close();
        return texts;
    }

    private static void Lay(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    /// <summary>Text of every TextBlock that isn't inside a collapsed/hidden element.</summary>
    private static List<string> Texts(DependencyObject root)
    {
        var texts = new List<string>();
        Walk(root);
        return texts;

        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return;
            }

            if (node is TextBlock { Text: { Length: > 0 } text })
            {
                texts.Add(text.Trim());
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    /// <summary>No DropShadowEffect / BlurEffect / any Effect: DualAudio runs next to games.</summary>
    private static void AssertNoEffects(DependencyObject node)
    {
        if (node is UIElement element)
        {
            element.Effect.Should().BeNull($"{element.GetType().Name} must not use an Effect");
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            AssertNoEffects(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static void RunSta(Action action) => RunSta<object?>(() =>
    {
        action();
        return null;
    });

    private static T RunSta<T>(Func<T> action)
    {
        Exception? failure = null;
        T result = default!;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }
}
