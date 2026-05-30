using System.ComponentModel;
using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Views.Dialogs;

public sealed partial class AdvancedOptionsDialog : ContentDialog, IDisposable
{
    private readonly MainShellViewModel viewModel;
    private bool isDisposed;

    public AdvancedOptionsDialog(MainShellViewModel viewModel, XamlRoot xamlRoot)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        XamlRoot = xamlRoot;

        WorkersBox.Value = viewModel.AdvancedWorkers;
        TimeoutBox.Value = viewModel.AdvancedTimeoutSeconds;
        StaleLockBox.Value = viewModel.AdvancedStaleLockMinutes;
        SyncAllBranchesBox.IsChecked = viewModel.AdvancedSyncAllBranches;
        StaleLockCleanupBox.IsChecked = viewModel.AdvancedNoStaleLockCleanup;
        VerboseReportBox.IsChecked = viewModel.AdvancedVerboseReport;
        InitMissingSubmodulesBox.IsChecked = viewModel.AdvancedInitMissingSubmodules;

        WorkersBox.ValueChanged += WorkersBox_ValueChanged;
        TimeoutBox.ValueChanged += TimeoutBox_ValueChanged;
        StaleLockBox.ValueChanged += StaleLockBox_ValueChanged;
        SyncAllBranchesBox.Checked += SyncAllBranchesBox_Checked;
        SyncAllBranchesBox.Unchecked += SyncAllBranchesBox_Unchecked;
        StaleLockCleanupBox.Checked += StaleLockCleanupBox_Checked;
        StaleLockCleanupBox.Unchecked += StaleLockCleanupBox_Unchecked;
        VerboseReportBox.Checked += VerboseReportBox_Checked;
        VerboseReportBox.Unchecked += VerboseReportBox_Unchecked;
        InitMissingSubmodulesBox.Checked += InitMissingSubmodulesBox_Checked;
        InitMissingSubmodulesBox.Unchecked += InitMissingSubmodulesBox_Unchecked;
        PrimaryButtonClick += AdvancedOptionsDialog_PrimaryButtonClick;
        Closed += AdvancedOptionsDialog_Closed;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;

        UpdateDialogState();
    }

    private void WorkersBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        viewModel.AdvancedWorkers = AdvancedOptionsDialogState.NormalizeNumberBoxValue(args.NewValue);
    }

    private void TimeoutBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        viewModel.AdvancedTimeoutSeconds = AdvancedOptionsDialogState.NormalizeNumberBoxValue(args.NewValue);
    }

    private void StaleLockBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        viewModel.AdvancedStaleLockMinutes = AdvancedOptionsDialogState.NormalizeNumberBoxValue(args.NewValue);
    }

    private void SyncAllBranchesBox_Checked(object sender, RoutedEventArgs e)
    {
        viewModel.AdvancedSyncAllBranches = true;
    }

    private void SyncAllBranchesBox_Unchecked(object sender, RoutedEventArgs e)
    {
        viewModel.AdvancedSyncAllBranches = false;
    }

    private void StaleLockCleanupBox_Checked(object sender, RoutedEventArgs e)
    {
        viewModel.AdvancedNoStaleLockCleanup = true;
    }

    private void StaleLockCleanupBox_Unchecked(object sender, RoutedEventArgs e)
    {
        viewModel.AdvancedNoStaleLockCleanup = false;
    }

    private void VerboseReportBox_Checked(object sender, RoutedEventArgs e)
    {
        viewModel.AdvancedVerboseReport = true;
    }

    private void VerboseReportBox_Unchecked(object sender, RoutedEventArgs e)
    {
        viewModel.AdvancedVerboseReport = false;
    }

    private void InitMissingSubmodulesBox_Checked(object sender, RoutedEventArgs e)
    {
        viewModel.AdvancedInitMissingSubmodules = true;
    }

    private void InitMissingSubmodulesBox_Unchecked(object sender, RoutedEventArgs e)
    {
        viewModel.AdvancedInitMissingSubmodules = false;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainShellViewModel.CanSaveAdvancedOptions)
            or nameof(MainShellViewModel.AdvancedOptionsStatusMessage)
            or nameof(MainShellViewModel.AdvancedOptionsErrorMessage)
            or nameof(MainShellViewModel.HasAdvancedOptionsStatus)
            or nameof(MainShellViewModel.HasAdvancedOptionsError))
        {
            UpdateDialogState();
        }
    }

    private async void AdvancedOptionsDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await viewModel.SaveAdvancedOptionsAsync();
            args.Cancel = viewModel.HasAdvancedOptionsError;
            UpdateDialogState();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void AdvancedOptionsDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        WorkersBox.ValueChanged -= WorkersBox_ValueChanged;
        TimeoutBox.ValueChanged -= TimeoutBox_ValueChanged;
        StaleLockBox.ValueChanged -= StaleLockBox_ValueChanged;
        isDisposed = true;
    }

    private void UpdateDialogState()
    {
        IsPrimaryButtonEnabled = viewModel.CanSaveAdvancedOptions;
        StatusBar.IsOpen = viewModel.HasAdvancedOptionsStatus;
        StatusBar.Message = viewModel.AdvancedOptionsStatusMessage;
        ErrorBar.IsOpen = viewModel.HasAdvancedOptionsError;
        ErrorBar.Message = viewModel.AdvancedOptionsErrorMessage;
    }

}
