using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SqlPhanos.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        // Remove IsFocusRequested logic for now (revert to pre-shortcut state)
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
