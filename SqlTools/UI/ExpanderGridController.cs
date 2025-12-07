using Microsoft.Xaml.Behaviors;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SqlTools.UI
{
    /// <summary>
    /// Behavior that allows an Expander control to (sort-of) control a row or column definition in a WPF layout grid,
    /// often in conjunction with a GridSplitter.
    /// Locates the Grid by searching upwards through the visible tree, then locates the associated Row or Column Definition within that grid
    /// where its x:Name property starts with the x:Name value from the Expander control.
    /// </summary>
    /// <remarks>
    /// Often, Expander controls are placed into layout grids and specific ColumnDefinition or RowDefinition objects should be essentially
    /// expanded and collapsed along with the associated Expander. This must be done by setting the Width (ColumnDefinition) or Height
    /// (RowDefinition) to GridLength.Auto when the Expander is Collapsed, then resetting it to the pre-collapsed value when Expanded. This
    /// behavior uses the x:Name attribute of the Expander to locate the associated ColumnDefinition or RowDefinition.
    /// <list type="number">
    /// <item>Put a layout Grid control into your XAML.</item>
    /// <item>Within that grid, identify a ColumnDefinition or RowDefinition that will contain the content associated with the Expander.</item>
    /// <item>Somewhere in the grid, put an Expander control with content within it. Make sure the Expander is surrounding the
    /// content you want to</item>
    /// <item>Set the x:Name prop of the Expander to something reasonable.</item>
    /// <item>Set the x:Name prop of the associated Grid or ColumnDefinition so that it starts with the same thing as the Expander. Ensure that
    /// the two name attributes are not identical (this will cause a compilation error). If the Expander is called 'FooVisible', then perhaps a good
    /// name for the associated row is 'FooVisibleRow'.</item>
    /// </list>
    /// </remarks>
    public class ExpanderGridControllerBehavior : Behavior<Expander>
    {
        private Grid grid;
        private GridLength previousGridLength = GridLength.Auto;
        private Func<GridLength> sizeGetter = null;
        private Action<GridLength> sizeSetter = null;

        public Grid FindGrid(FrameworkElement thingWithParent)
        {
            var parentFrameworkElement = thingWithParent.Parent as FrameworkElement;
            if (parentFrameworkElement != null)
            {
                var grid = parentFrameworkElement as Grid;
                if (grid != null)
                {
                    return grid;
                }
                return FindGrid(parentFrameworkElement);
            }
            return null;
        }

        protected override void OnAttached()
        {
            this.grid = FindGrid(AssociatedObject);
            if (grid != null)
            {
                if (string.IsNullOrWhiteSpace(AssociatedObject.Name))
                {
                    throw new InvalidOperationException(
                        string.Format("The Expander object must have a non-empty x:Name attribute to be used with this behavior."));
                }
                // find the columndef or rowdef that we think matches our associated object
                var col = grid.ColumnDefinitions.Where(cd => cd.Name.StartsWith(AssociatedObject.Name)).FirstOrDefault();
                var row = grid.RowDefinitions.Where(rd => rd.Name.StartsWith(AssociatedObject.Name)).FirstOrDefault();
                if (row == null && col == null)
                {
                    throw new InvalidOperationException(
                        string.Format("Could not find a ColumnDefinition or RowDefinition with an x:Name attribute that starts with {0}", AssociatedObject.Name));
                }
                if (row != null)
                {
                    sizeSetter = gl => row.Height = gl;
                    sizeGetter = () => row.Height;
                }
                else
                {
                    if (col != null)
                    {
                        sizeSetter = gl => col.Width = gl;
                        sizeGetter = () => col.Width;
                    }
                }
                AssociatedObject.Collapsed += (_, __) =>
                {
                    var len = sizeGetter();
                    previousGridLength = len;
                    sizeSetter(GridLength.Auto);
                };
                AssociatedObject.Expanded += (_, __) =>
                {
                    sizeSetter(previousGridLength);
                };
            }
            else
            {
                throw new InvalidOperationException("Unable to locate parent grid object.");
            }
        }
    }
}