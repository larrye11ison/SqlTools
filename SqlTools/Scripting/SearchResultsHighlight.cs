using ICSharpCode.AvalonEdit.Document;
using SqlTools.Shell;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace SqlTools.Scripting
{
    /// <summary>
    /// Used for highlighting search results in the AvalonEdit code view.
    /// </summary>
    public class SearchResultsHighlight : ICSharpCode.AvalonEdit.Rendering.DocumentColorizingTransformer
    {
        //private string description;

        public SearchResultsHighlight(string description)
        {
            //this.description = description;
            ForegroundBrush = Brushes.Yellow;
            BackgroundBrush = Brushes.Black;
        }

        public Brush BackgroundBrush { get; set; }

        public ICollection<FoundTextResult> FindResults { get; set; }

        public Brush ForegroundBrush { get; set; }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (FindResults == null || FindResults.Where(fr => fr != null).Count() == 0)
            {
                return;
            }
            int lineStartOffset = line.Offset;
            int lineEndOffset = line.EndOffset;

            var applicableResults =
                (from fr in FindResults
                 where fr.StartOffset >= lineStartOffset
                     && fr.StartOffset <= lineEndOffset
                 select fr).ToList();

            foreach (var item in applicableResults)
            {
                ChangeLinePart(
                    item.StartOffset, // startOffset
                    item.EndOffset, // endOffset
                    (element) =>
                    {
                        element.TextRunProperties.SetForegroundBrush(ForegroundBrush);
                        element.TextRunProperties.SetBackgroundBrush(BackgroundBrush);
                    });
            }
        }
    }
}