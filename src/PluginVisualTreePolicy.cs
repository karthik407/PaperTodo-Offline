using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PaperTodo;

internal static class PluginVisualTreePolicy
{
    public static bool IsSupportedPureWpfTree(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }
            if (IsUnsupportedSurface(current))
            {
                return false;
            }

            try
            {
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var index = 0; index < count; index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }
            catch
            {
                // A non-Visual DependencyObject can still expose logical children below.
            }

            try
            {
                foreach (var child in LogicalTreeHelper.GetChildren(current))
                {
                    if (child is DependencyObject dependencyObject)
                    {
                        pending.Push(dependencyObject);
                    }
                }
            }
            catch
            {
                // Treat an unavailable logical tree as a leaf. Unsupported roots are still caught.
            }
        }
        return true;
    }

    private static bool IsUnsupportedSurface(DependencyObject element)
    {
        if (element is Window or HwndHost)
        {
            return true;
        }

        return false;
    }
}
