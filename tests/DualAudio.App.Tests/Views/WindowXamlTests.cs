using System.Runtime.ExceptionServices;
using DualAudio.App.Tests.ViewModels;
using DualAudio.App.Views;

namespace DualAudio.App.Tests.Views;

/// <summary>Loads the windows' XAML (InitializeComponent) on an STA thread, so markup errors fail here, not at start-up.</summary>
public sealed class WindowXamlTests
{
    [Fact]
    public async Task Onboarding_window_xaml_loads()
    {
        await using var f = await OnboardingFixture.CreateAsync(); // check rows present, so the item template is realized
        RunSta(() => Load(new OnboardingWindow(f.Vm)));
    }

    [Fact]
    public async Task Main_window_xaml_loads()
    {
        await using var f = await DashboardFixture.CreateAsync();
        RunSta(() => Load(new MainWindow(f.Vm)));
    }

    /// <summary>Lays the content out without showing a window, which also instantiates data templates.</summary>
    private static void Load(System.Windows.Window window)
    {
        var content = (System.Windows.FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(520, 680));
        content.Arrange(new System.Windows.Rect(0, 0, 520, 680));
        content.UpdateLayout();
        window.Close();
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
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
    }
}
