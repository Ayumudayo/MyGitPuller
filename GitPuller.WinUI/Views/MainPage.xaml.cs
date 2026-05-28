using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Views;

public sealed partial class MainPage : Page
{
    public MainShellViewModel ViewModel { get; } = MainShellViewModel.CreateSample();

    public MainPage()
    {
        InitializeComponent();
    }
}
