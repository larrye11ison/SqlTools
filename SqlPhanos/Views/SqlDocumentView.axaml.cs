using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SqlPhanos.Views;

public partial class SqlDocumentView : UserControl
{
    public SqlDocumentView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}