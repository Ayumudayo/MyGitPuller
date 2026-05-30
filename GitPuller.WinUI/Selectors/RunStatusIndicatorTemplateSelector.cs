using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Selectors;

public sealed class RunStatusIndicatorTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ReadyTemplate { get; set; }
    public DataTemplate? RunningTemplate { get; set; }
    public DataTemplate? CompletedTemplate { get; set; }
    public DataTemplate? ReviewRequiredTemplate { get; set; }
    public DataTemplate? InterruptedTemplate { get; set; }
    public DataTemplate? CanceledTemplate { get; set; }
    public DataTemplate? FailedTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        var kind = item is RunStatusIndicatorViewModel indicator
            ? indicator.Kind
            : RunStatusIndicatorKind.Ready;
        var template = kind switch
        {
            RunStatusIndicatorKind.Running => RunningTemplate,
            RunStatusIndicatorKind.Completed => CompletedTemplate,
            RunStatusIndicatorKind.ReviewRequired => ReviewRequiredTemplate,
            RunStatusIndicatorKind.Interrupted => InterruptedTemplate,
            RunStatusIndicatorKind.Canceled => CanceledTemplate,
            RunStatusIndicatorKind.Failed => FailedTemplate,
            _ => ReadyTemplate
        };

        return template
            ?? throw new InvalidOperationException("Run status indicator templates must be configured for all run states.");
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}
