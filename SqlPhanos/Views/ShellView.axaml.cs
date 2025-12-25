using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SqlPhanos.ViewModels;
using System.Diagnostics;

namespace SqlPhanos.Views;

public partial class ShellView : Window
{
    public ShellView()
    {
        Debug.WriteLine("=== ShellView constructor ===");
        InitializeComponent();
        
        var viewModel = new ShellViewModel();
        DataContext = viewModel;
        
        Debug.WriteLine($"ShellView DataContext set to: {DataContext?.GetType().Name}");
    }
    
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        Debug.WriteLine("ShellView XAML loaded");
    }
}
