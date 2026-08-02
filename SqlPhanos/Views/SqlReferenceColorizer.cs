using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using SqlPhanos.ViewModels;
using System;
using System.Collections.Generic;

namespace SqlPhanos.Views;

internal sealed class SqlReferenceColorizer : DocumentColorizingTransformer
{
    private readonly Func<IReadOnlyList<SqlDocumentReference>> _getReferences;

    public SqlReferenceColorizer(Func<IReadOnlyList<SqlDocumentReference>> getReferences)
    {
        _getReferences = getReferences;
    }

    public IBrush LinkBrush { get; set; } = Brushes.DodgerBlue;

    public IBrush LinkedServerBrush { get; set; } = Brushes.DarkOrange;

    public IBrush UnresolvedBrush { get; set; } = Brushes.IndianRed;

    protected override void ColorizeLine(DocumentLine line)
    {
        var lineStart = line.Offset;
        var lineEnd = line.EndOffset;

        foreach (var reference in _getReferences())
        {
            var referenceStart = reference.Offset;
            var referenceEnd = reference.Offset + reference.Length;
            var start = Math.Max(lineStart, referenceStart);
            var end = Math.Min(lineEnd, referenceEnd);
            if (start >= end)
            {
                continue;
            }

            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetForegroundBrush(
                    reference.IsClickable
                        ? LinkBrush
                        : reference.IsExternalReference
                            ? LinkedServerBrush
                            : UnresolvedBrush);
                element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            });
        }
    }
}
