using System;
using System.Reactive.Linq;
using System.Windows.Controls;
using System.Windows.Input;

namespace SqlTools.Shell
{
    public partial class DBSearchResultsView : UserControl
    {
        public DBSearchResultsView()
        {
            InitializeComponent();

            DatabaseObjects.PreviewKeyDown += dbObjectsGrid_PreviewKeyDown;

            Observable.FromEventPattern<TextChangedEventArgs>(ResultsFilter, "TextChanged")
                .Throttle(TimeSpan.FromMilliseconds(300))
                .ObserveOnDispatcher()
                .Select(tb => ResultsFilter.Text)
                .DistinctUntilChanged()
                .Subscribe(b =>
                    {
                        var dc = this.DataContext as DBSearchResultsViewModel;
                        if (dc != null)
                        {
                            dc.ResultsFilterChanged(b);
                        }
                    });
        }

        private void dbObjectsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // TODO: should use a trigger action or something instead of this event...
            if (e.Key == Key.Enter)
            {
                var turd = (sender as DataGrid).SelectedItem as DBObjectViewModel;
                if (turd != null && turd.CanScriptObject)
                {
                    e.Handled = true;
                    turd.ScriptObject();
                }
            }
        }
    }
}