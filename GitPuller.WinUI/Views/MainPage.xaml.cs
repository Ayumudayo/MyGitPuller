using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Views;

public sealed partial class MainPage : Page
{
    public MainShellViewModel ViewModel { get; } = MainShellViewModel.CreateSample();

    public MainPage()
    {
        InitializeComponent();
    }

    private void CategoryNavigation_Loaded(object sender, RoutedEventArgs e)
    {
        CategoryNavigation.SelectedItem ??= AllRepositoriesItem;
    }

    private void CategoryNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        ViewModel.SelectedCategory = tag == "__all"
            ? null
            : ViewModel.Categories.FirstOrDefault(category =>
                string.Equals(category.Name, tag, StringComparison.OrdinalIgnoreCase));
    }
}
