using SqlTools.UI;
using System.Windows.Controls;

namespace SqlTools.DatabaseConnections
{
    /// <summary>
    /// Interaction logic for SqlConnectionView.xaml
    /// </summary>
    public partial class SqlConnectionView : UserControl, IMainControlFocusable
    {
        public SqlConnectionView()
        {
            InitializeComponent();
        }

        public void SetFocusOnMainControl()
        {
            ObjectNameQuery.Focus();
            ObjectNameQuery.SelectAll();
        }
    }
}