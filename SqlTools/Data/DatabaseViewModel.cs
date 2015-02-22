using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Media;

namespace SqlTools.Data
{
    public class DatabaseViewModel : Caliburn.Micro.PropertyChangedBase
    {
        public Brush BorderBrush
        {
            get
            {
                if (IsSelected)
                    return SystemColors.HighlightBrush;
                else
                    return Brushes.Transparent;
            }
        }

        [Key]
        public string db_name { get; set; }

        public bool IsSelected { get; set; }

        public string owner_name { get; set; }

        public string quote_name { get; set; }

        public string state_desc { get; set; }

        public override string ToString()
        {
            return string.Format("{0} ({1})", db_name, owner_name);
        }
    }
}