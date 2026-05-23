using Dock.Model.Avalonia.Controls;
using Dock.Model.Mvvm;

namespace SqlPhanos.ViewModels
{
    public sealed class DockableWrapper<TViewModel> : Tool where TViewModel : class
    {
        public DockableWrapper(string id, string title, TViewModel viewModel)
        {
            Id = id;
            Title = title;
            ViewModel = viewModel;
        }

        public TViewModel ViewModel { get; }
    }
}