using System.ComponentModel;
using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Views;

public sealed partial class MainPage : Page
{
    public MainShellViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = MainShellViewModel.CreateDefault(
            new DispatcherQueueViewModelDispatcher(DispatcherQueue.GetForCurrentThread()));

        InitializeComponent();

        Loaded += MainPage_Loaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateRetryButtonVisibility();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        await ViewModel.InitializeAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainShellViewModel.HasSelectedResult)
            or nameof(MainShellViewModel.IsSelectedResultRetryPrimary)
            or nameof(MainShellViewModel.IsSelectedResultRetrySecondary)
            or nameof(MainShellViewModel.SelectedResultCanRetry))
        {
            UpdateRetryButtonVisibility();
        }
    }

    private void UpdateRetryButtonVisibility()
    {
        if (PrimaryRetrySelectedDetailButton is null || SecondaryRetrySelectedDetailButton is null)
        {
            return;
        }

        var showPrimary = ViewModel.HasSelectedResult && ViewModel.IsSelectedResultRetryPrimary;
        var showSecondary = ViewModel.HasSelectedResult && !ViewModel.IsSelectedResultRetryPrimary;

        PrimaryRetrySelectedDetailButton.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryRetrySelectedDetailButton.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class DispatcherQueueViewModelDispatcher : IViewModelDispatcher
    {
        private readonly DispatcherQueue dispatcherQueue;

        public DispatcherQueueViewModelDispatcher(DispatcherQueue dispatcherQueue)
        {
            this.dispatcherQueue = dispatcherQueue;
        }

        public void Enqueue(Action action)
        {
            if (dispatcherQueue.HasThreadAccess)
            {
                action();
                return;
            }

            dispatcherQueue.TryEnqueue(() => action());
        }
    }
}
