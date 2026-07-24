using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace SqlPhanos.Services;

public static class PaneFocusTracker
{
    private static readonly Dictionary<string, System.WeakReference<IInputElement>> LastFocused = new();

    public static string? CurrentPaneId { get; set; }

    public static void RecordFocus(string paneId, IInputElement element)
    {
        CurrentPaneId = paneId;
        LastFocused[paneId] = new System.WeakReference<IInputElement>(element);
    }

    public static bool TryGetLastFocus(string paneId, out IInputElement? element)
    {
        element = null;

        if (!LastFocused.TryGetValue(paneId, out var weakRef))
        {
            return false;
        }

        if (!weakRef.TryGetTarget(out var target))
        {
            return false;
        }

        if (target is not Visual visual || !visual.IsAttachedToVisualTree())
        {
            return false;
        }

        element = target;
        return true;
    }
}
