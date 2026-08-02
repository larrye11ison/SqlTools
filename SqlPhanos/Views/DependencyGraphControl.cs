using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SqlPhanos.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SqlPhanos.Views;

public sealed class DependencyGraphNodeEventArgs(
    DependencyGraphNodeViewModel node) : EventArgs
{
    public DependencyGraphNodeViewModel Node { get; } = node;
}

public sealed class DependencyGraphControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<DependencyGraphNodeViewModel>> NodesProperty =
        AvaloniaProperty.Register<DependencyGraphControl, IReadOnlyList<DependencyGraphNodeViewModel>>(
            nameof(Nodes),
            Array.Empty<DependencyGraphNodeViewModel>());

    public static readonly StyledProperty<IReadOnlyList<DependencyGraphEdgeViewModel>> EdgesProperty =
        AvaloniaProperty.Register<DependencyGraphControl, IReadOnlyList<DependencyGraphEdgeViewModel>>(
            nameof(Edges),
            Array.Empty<DependencyGraphEdgeViewModel>());

    public static readonly StyledProperty<DependencyGraphNodeViewModel?> SelectedNodeProperty =
        AvaloniaProperty.Register<DependencyGraphControl, DependencyGraphNodeViewModel?>(
            nameof(SelectedNode),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private const double NodeWidth = 190;
    private const double NodeHeight = 80;
    private const double LayerGap = 105;
    private const double NodeGap = 24;
    private readonly Dictionary<string, Rect> _nodeBounds = new(StringComparer.Ordinal);
    private Point _pan = new(24, 24);
    private double _zoom = 1;
    private Point? _dragStart;
    private Point _panAtDragStart;

    static DependencyGraphControl()
    {
        AffectsRender<DependencyGraphControl>(
            NodesProperty,
            EdgesProperty,
            SelectedNodeProperty);
    }

    public DependencyGraphControl()
    {
        ClipToBounds = true;
        Focusable = true;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += OnPointerExited;
    }

    public IReadOnlyList<DependencyGraphNodeViewModel> Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public IReadOnlyList<DependencyGraphEdgeViewModel> Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    public DependencyGraphNodeViewModel? SelectedNode
    {
        get => GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    // Node activation is via the two in-node buttons (drawn per-node, hit-tested in
    // OnPointerPressed) rather than double-click, so which action the user wants is explicit.
    public event EventHandler<DependencyGraphNodeEventArgs>? ScriptRequested;
    public event EventHandler<DependencyGraphNodeEventArgs>? DependenciesRequested;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(
            new SolidColorBrush(Color.Parse("#111111")),
            new Rect(Bounds.Size));

        BuildLayout();
        var nodeById = Nodes.ToDictionary(static node => node.Id, StringComparer.Ordinal);
        foreach (var edge in Edges)
        {
            if (!_nodeBounds.TryGetValue(edge.SourceNodeId, out var source) ||
                !_nodeBounds.TryGetValue(edge.TargetNodeId, out var target))
            {
                continue;
            }

            DrawEdge(
                context,
                Transform(source.Center),
                Transform(target.Center),
                edge.HasWarning);
        }

        foreach (var pair in _nodeBounds)
        {
            if (nodeById.TryGetValue(pair.Key, out var node))
            {
                DrawNode(context, node, Transform(pair.Value));
            }
        }
    }

    private void BuildLayout()
    {
        _nodeBounds.Clear();
        foreach (var layer in Nodes
                     .GroupBy(static node => node.Depth)
                     .OrderBy(static group => group.Key))
        {
            var ordered = layer
                .OrderByDescending(static node => node.IsRoot)
                .ThenBy(static node => node.Database, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                _nodeBounds[ordered[index].Id] = new Rect(
                    layer.Key * (NodeWidth + LayerGap),
                    index * (NodeHeight + NodeGap),
                    NodeWidth,
                    NodeHeight);
            }
        }
    }

    private void DrawNode(
        DrawingContext context,
        DependencyGraphNodeViewModel node,
        Rect bounds)
    {
        var fill = node.IsRoot
            ? Color.Parse("#075985")
            : node.IsExternal
                ? Color.Parse("#713F12")
                : node.IsUnresolved
                    ? Color.Parse("#7F1D1D")
                    : Color.Parse("#262626");
        var border = ReferenceEquals(node, SelectedNode)
            ? Color.Parse("#38BDF8")
            : node.HasCoverageWarning
                ? Color.Parse("#FB923C")
                : Color.Parse("#737373");

        context.DrawRectangle(
            new SolidColorBrush(fill),
            new Pen(new SolidColorBrush(border), ReferenceEquals(node, SelectedNode) ? 3 : 1),
            bounds,
            6,
            6);

        var title = new FormattedText(
            node.DisplayName,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            13 * _zoom,
            Brushes.White)
        {
            MaxTextWidth = Math.Max(20, bounds.Width - 16),
            MaxTextHeight = 22 * _zoom,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var subtitle = new FormattedText(
            node.Subtitle,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            10 * _zoom,
            new SolidColorBrush(Color.Parse("#D4D4D4")))
        {
            MaxTextWidth = Math.Max(20, bounds.Width - 16),
            MaxTextHeight = 18 * _zoom,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(title, new Point(bounds.X + 8, bounds.Y + 7));
        context.DrawText(subtitle, new Point(bounds.X + 8, bounds.Y + 32 * _zoom));

        var (scriptButton, dependenciesButton) = GetButtonBounds(bounds);
        var buttonFill = Color.Parse(node.HasIndexedObject ? "#374151" : "#1F2937");
        IBrush buttonText = node.HasIndexedObject
            ? Brushes.White
            : new SolidColorBrush(Color.Parse("#6B7280"));
        DrawButton(context, scriptButton, "Script", buttonFill, buttonText);
        DrawButton(context, dependenciesButton, "Dependencies", buttonFill, buttonText);
    }

    // Proportional to whatever rect is passed in, so the same math works for both hit-testing
    // (against the untransformed graph-space rect in _nodeBounds) and drawing (against the
    // pan/zoom-transformed screen-space rect) without needing to separately account for zoom.
    private static (Rect Script, Rect Dependencies) GetButtonBounds(Rect nodeBounds)
    {
        var padding = nodeBounds.Width * 0.04;
        var stripHeight = nodeBounds.Height * 0.30;
        var stripTop = nodeBounds.Bottom - stripHeight - padding;
        var gap = nodeBounds.Width * 0.04;
        var buttonWidth = (nodeBounds.Width - (padding * 2) - gap) / 2;
        var scriptRect = new Rect(nodeBounds.X + padding, stripTop, buttonWidth, stripHeight);
        var dependenciesRect = new Rect(scriptRect.Right + gap, stripTop, buttonWidth, stripHeight);
        return (scriptRect, dependenciesRect);
    }

    private static void DrawButton(
        DrawingContext context, Rect bounds, string label, Color fill, IBrush textBrush)
    {
        context.DrawRectangle(new SolidColorBrush(fill), null, bounds, 3, 3);
        var text = new FormattedText(
            label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            Math.Clamp(bounds.Height * 0.45, 7, 10),
            textBrush)
        {
            MaxTextWidth = Math.Max(4, bounds.Width - 4),
            MaxTextHeight = bounds.Height,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(text, new Point(bounds.X + 2, bounds.Y + Math.Max(0, (bounds.Height - text.Height) / 2)));
    }

    private static void DrawEdge(
        DrawingContext context,
        Point source,
        Point target,
        bool warning)
    {
        var color = warning ? Color.Parse("#FB923C") : Color.Parse("#60A5FA");
        var pen = new Pen(new SolidColorBrush(color), warning ? 2 : 1.5);
        context.DrawLine(pen, source, target);

        var vector = source - target;
        var length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
        if (length < 1)
        {
            return;
        }

        var unit = new Vector(vector.X / length, vector.Y / length);
        var perpendicular = new Vector(-unit.Y, unit.X);
        var tip = target;
        context.DrawLine(pen, tip, tip + (unit * 12) + (perpendicular * 5));
        context.DrawLine(pen, tip, tip + (unit * 12) - (perpendicular * 5));
    }

    private Rect Transform(Rect rect) =>
        new(
            (rect.X * _zoom) + _pan.X,
            (rect.Y * _zoom) + _pan.Y,
            rect.Width * _zoom,
            rect.Height * _zoom);

    private Point Transform(Point point) =>
        new((point.X * _zoom) + _pan.X, (point.Y * _zoom) + _pan.Y);

    private Point Untransform(Point point) =>
        new((point.X - _pan.X) / _zoom, (point.Y - _pan.Y) / _zoom);

    private DependencyGraphNodeViewModel? HitTestNode(Point point)
    {
        var (node, _) = HitTestNodeWithBounds(point);
        return node;
    }

    private (DependencyGraphNodeViewModel? Node, Rect Bounds) HitTestNodeWithBounds(Point point)
    {
        var graphPoint = Untransform(point);
        var entry = _nodeBounds.FirstOrDefault(pair => pair.Value.Contains(graphPoint));
        if (entry.Key is null)
        {
            return (null, default);
        }

        var node = Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, entry.Key, StringComparison.Ordinal));
        return (node, entry.Value);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12 : 0.89), 0.45, 2.5);
        var point = e.GetPosition(this);
        var graphPoint = new Point(
            (point.X - _pan.X) / oldZoom,
            (point.Y - _pan.Y) / oldZoom);
        _pan = new Point(
            point.X - (graphPoint.X * _zoom),
            point.Y - (graphPoint.Y * _zoom));
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetPosition(this);
        var (node, bounds) = HitTestNodeWithBounds(point);
        if (node is not null)
        {
            SelectedNode = node;
            if (node.HasIndexedObject)
            {
                var graphPoint = Untransform(point);
                var (scriptButton, dependenciesButton) = GetButtonBounds(bounds);
                if (scriptButton.Contains(graphPoint))
                {
                    ScriptRequested?.Invoke(this, new DependencyGraphNodeEventArgs(node));
                }
                else if (dependenciesButton.Contains(graphPoint))
                {
                    DependenciesRequested?.Invoke(this, new DependencyGraphNodeEventArgs(node));
                }
            }
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _dragStart = point;
        _panAtDragStart = _pan;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start)
        {
            var node = HitTestNode(e.GetPosition(this));
            Cursor = node is null
                ? new Cursor(StandardCursorType.Arrow)
                : new Cursor(StandardCursorType.Hand);
            // DisplayName/Subtitle are drawn truncated to the node's fixed width - this is the
            // only way to recover the full name short of widening every node.
            ToolTip.SetTip(this, node?.TooltipText);
            ToolTip.SetIsOpen(this, node is not null);
            return;
        }

        var current = e.GetPosition(this);
        _pan = new Point(
            _panAtDragStart.X + current.X - start.X,
            _panAtDragStart.Y + current.Y - start.Y);
        InvalidateVisual();
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        ToolTip.SetIsOpen(this, false);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStart = null;
        e.Pointer.Capture(null);
    }
}
