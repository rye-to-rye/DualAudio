using DualAudio.App.ViewModels;

namespace DualAudio.App.Views;

public partial class OnboardingWindow
{
    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
