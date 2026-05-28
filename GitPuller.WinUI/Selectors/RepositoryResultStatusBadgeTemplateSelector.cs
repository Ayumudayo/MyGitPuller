using GitPuller_WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitPuller_WinUI.Selectors;

public sealed class RepositoryResultStatusBadgeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FailedTemplate { get; set; }
    public DataTemplate? WarningTemplate { get; set; }
    public DataTemplate? UpdatedTemplate { get; set; }
    public DataTemplate? CleanTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        var template = item is RepositoryResultViewModel result
            ? result.Status switch
            {
                RepositoryResultStatus.Failed => FailedTemplate,
                RepositoryResultStatus.Warning => WarningTemplate,
                RepositoryResultStatus.Updated => UpdatedTemplate,
                _ => CleanTemplate
            }
            : CleanTemplate;

        return template
            ?? throw new InvalidOperationException("Status badge templates must be configured for all repository result states.");
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}
