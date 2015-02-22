using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Diagnostics.Contracts;
using System.Linq;

namespace SqlTools.Shell
{
    public abstract class DocumentColorizingTransformer : ColorizingTransformer
    {
        private DocumentLine currentDocumentLine;
        private int currentDocumentLineStartOffset, currentDocumentLineEndOffset;
        private int firstLineStart;

        /// <summary>
        /// Gets the current ITextRunConstructionContext.
        /// </summary>
        protected ITextRunConstructionContext CurrentContext { get; private set; }

        /// <summary>
        /// Changes a part of the current document line.
        /// </summary>
        /// <param name="startOffset">Start offset of the region to change</param>
        /// <param name="endOffset">End offset of the region to change</param>
        /// <param name="action">Action that changes an individual <see cref="VisualLineElement"/>.</param>
        protected void ChangeLinePart(int startOffset, int endOffset, Action<VisualLineElement> action)
        {
            if (startOffset < currentDocumentLineStartOffset || startOffset > currentDocumentLineEndOffset)
                throw new ArgumentOutOfRangeException("startOffset", startOffset, String.Format("Value must be between {0} and {1}", currentDocumentLineStartOffset, currentDocumentLineEndOffset));
            if (endOffset < startOffset || endOffset > currentDocumentLineEndOffset)
                throw new ArgumentOutOfRangeException("endOffset", endOffset, String.Format("Value must be between {0} and {1}", startOffset, currentDocumentLineEndOffset));
            VisualLine vl = this.CurrentContext.VisualLine;
            int visualStart = vl.GetVisualColumn(startOffset - firstLineStart);
            int visualEnd = vl.GetVisualColumn(endOffset - firstLineStart);
            if (visualStart < visualEnd)
            {
                ChangeVisualElements(visualStart, visualEnd, action);
            }
        }

        /// <inheritdoc/>
        protected override void Colorize(ITextRunConstructionContext context)
        {
            Contract.Requires(context != null);

            //if (context == null)
            //    throw new ArgumentNullException("context");
            this.CurrentContext = context;

            currentDocumentLine = context.VisualLine.FirstDocumentLine;
            firstLineStart = currentDocumentLineStartOffset = currentDocumentLine.Offset;
            currentDocumentLineEndOffset = currentDocumentLineStartOffset + currentDocumentLine.Length;

            if (context.VisualLine.FirstDocumentLine == context.VisualLine.LastDocumentLine)
            {
                ColorizeLine(currentDocumentLine);
            }
            else
            {
                ColorizeLine(currentDocumentLine);

                // ColorizeLine modifies the visual line elements, loop through a copy of the line elements
                foreach (VisualLineElement e in context.VisualLine.Elements.ToArray())
                {
                    int elementOffset = firstLineStart + e.RelativeTextOffset;
                    if (elementOffset >= currentDocumentLineEndOffset)
                    {
                        currentDocumentLine = context.Document.GetLineByOffset(elementOffset);
                        currentDocumentLineStartOffset = currentDocumentLine.Offset;
                        currentDocumentLineEndOffset = currentDocumentLineStartOffset + currentDocumentLine.Length;
                        ColorizeLine(currentDocumentLine);
                    }
                }
            }
            currentDocumentLine = null;
            this.CurrentContext = null;
        }

        /// <summary>
        /// Override this method to colorize an individual document line.
        /// </summary>
        protected abstract void ColorizeLine(DocumentLine line);
    }
}