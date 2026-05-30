namespace GitPuller_WinUI.ViewModels;

public enum RunStatusIndicatorKind
{
    Ready,
    Running,
    Completed,
    ReviewRequired,
    Interrupted,
    Canceled,
    Failed
}

public sealed class RunStatusIndicatorViewModel
{
    public RunStatusIndicatorViewModel(string text, RunStatusIndicatorKind kind)
    {
        Text = string.IsNullOrWhiteSpace(text) ? "-" : text;
        Kind = kind;
    }

    public string Text { get; }
    public RunStatusIndicatorKind Kind { get; }
}
