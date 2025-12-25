using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using ReactiveUI;
using SqlPhanos.ViewModels;
using System;
using System.Reactive.Linq;

namespace SqlPhanos.Views;

public partial class SearchView : ReactiveUserControl<SearchViewModel>
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
