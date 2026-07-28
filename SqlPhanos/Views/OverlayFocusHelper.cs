using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SqlPhanos.Views;

/// <summary>
/// Wires an in-tab overlay (a Border/Grid shown via IsVisible="{Binding PendingX}") so its
/// default action button gets keyboard focus the moment the overlay appears. These overlays are
/// not real modal Windows - nothing moves keyboard focus into them on its own, so without this,
/// focus is left wherever it was in the (now IsEnabled="False") control behind the overlay,
/// making the dialog unreachable from the keyboard until the user reaches for the mouse.
/// </summary>
internal static class OverlayFocusHelper
{
    public static void FocusOnShow(Control overlay, Control target)
    {
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty || !overlay.IsVisible)
            {
                return;
            }

            // The overlay's IsVisible flip and this button's own layout haven't necessarily
            // settled yet at this point - same reasoning as SearchResultsView's identical
            // Dispatcher deferral for its own late-binding focus target.
            Dispatcher.UIThread.Post(() => target.Focus(), DispatcherPriority.Input);
        };
    }
}
