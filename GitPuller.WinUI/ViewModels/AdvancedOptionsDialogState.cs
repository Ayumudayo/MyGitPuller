namespace GitPuller_WinUI.ViewModels;

public static class AdvancedOptionsDialogState
{
    public static int NormalizeNumberBoxValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Round(value));
    }
}
