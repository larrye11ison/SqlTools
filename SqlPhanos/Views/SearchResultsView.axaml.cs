using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SqlPhanos.Views;

public partial class SearchResultsView : UserControl
{
    public SearchResultsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
