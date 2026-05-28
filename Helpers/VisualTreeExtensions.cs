using System.Windows;
using System.Windows.Media;

namespace TelegaScan.Helpers;

internal static class VisualTreeExtensions
{
    /// <summary>
    /// Ищет предка в визуальном и логическом дереве (нужно для Run/Inlines в TextBlock).
    /// </summary>
    public static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match) return match;
            child = GetParent(child);
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual)
        {
            var visualParent = VisualTreeHelper.GetParent(child);
            if (visualParent != null) return visualParent;
        }

        return LogicalTreeHelper.GetParent(child)
               ?? (child is FrameworkContentElement fce ? fce.Parent : null);
    }
}
