using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GitPuller_WinUI.Controls;

public sealed class PaneResizeHandle : Control
{
    private bool pointerInside;

    public PaneResizeHandle()
    {
        Opacity = 0.20;
        PointerEntered += PaneResizeHandle_PointerEntered;
        PointerExited += PaneResizeHandle_PointerExited;
        PointerCaptureLost += PaneResizeHandle_PointerCaptureLost;
        PointerPressed += PaneResizeHandle_PointerPressed;
        PointerReleased += PaneResizeHandle_PointerReleased;
    }

    public InputSystemCursorShape CursorShape { get; set; } = InputSystemCursorShape.SizeWestEast;

    private void PaneResizeHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        pointerInside = true;
        ProtectedCursor = InputSystemCursor.Create(CursorShape);
        Opacity = 0.65;
    }

    private void PaneResizeHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        pointerInside = false;
        ProtectedCursor = null;
        Opacity = 0.20;
    }

    private void PaneResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = null;
        Opacity = pointerInside ? 0.65 : 0.20;
    }

    private void PaneResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(CursorShape);
        Opacity = 0.85;
    }

    private void PaneResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Opacity = pointerInside ? 0.65 : 0.20;
    }
}
