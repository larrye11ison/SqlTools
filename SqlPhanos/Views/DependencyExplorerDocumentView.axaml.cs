using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Views;

public partial class DependencyExplorerDocumentView : UserControl
{
    private DependencyGraphControl? _graph;

    public DependencyExplorerDocumentView()
    {
        InitializeComponent();
        _graph = this.FindControl<DependencyGraphControl>("Graph");
        if (_graph is not null)
        {
            _graph.ScriptRequested += OnGraphScriptRequested;
            _graph.DependenciesRequested += OnGraphDependenciesRequested;
        }
        DetachedFromVisualTree += (_, _) =>
        {
            if (_graph is not null)
            {
                _graph.ScriptRequested -= OnGraphScriptRequested;
                _graph.DependenciesRequested -= OnGraphDependenciesRequested;
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnGraphScriptRequested(
        object? sender,
        DependencyGraphNodeEventArgs e)
    {
        if (DataContext is DependencyExplorerDocumentViewModel viewModel)
        {
            viewModel.OpenNodeCommand.Execute(e.Node);
        }
    }

    private void OnGraphDependenciesRequested(
        object? sender,
        DependencyGraphNodeEventArgs e)
    {
        if (DataContext is DependencyExplorerDocumentViewModel viewModel)
        {
            viewModel.OpenNodeDependenciesCommand.Execute(e.Node);
        }
    }
}
