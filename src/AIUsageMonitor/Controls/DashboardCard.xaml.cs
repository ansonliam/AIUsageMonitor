using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIUsageMonitor.ViewModels;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace AIUsageMonitor.Controls;

// Drag/resize behaviour for one dashboard card. Both Thumbs only give live visual feedback during
// the drag (a RenderTransform for moving, an explicit Width/Height for resizing) and only ask the
// owning DashboardLayoutViewModel to actually commit a new Row/Column/RowSpan/ColumnSpan once
// dragging finishes (DragCompleted) - see DashboardLayoutViewModel.TryMoveCard/TryResizeCard for
// the collision check and snap-back-on-conflict behaviour. Committing on every DragDelta instead
// would mean re-checking collisions (and re-saving to disk) many times per second and would fight
// with the live preview transform.
public partial class DashboardCard : WpfUserControl
{
    private double _moveAccumulatedX;
    private double _moveAccumulatedY;
    private double _resizeAccumulatedX;
    private double _resizeAccumulatedY;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeCellWidth;
    private double _resizeCellHeight;
    private int _resizePreviewRowSpan;
    private int _resizePreviewColumnSpan;

    public DashboardCard()
    {
        InitializeComponent();
    }

    private void MoveThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _moveAccumulatedX = 0;
        _moveAccumulatedY = 0;
    }

    private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _moveAccumulatedX += e.HorizontalChange;
        _moveAccumulatedY += e.VerticalChange;
        DragPreviewTransform.X = _moveAccumulatedX;
        DragPreviewTransform.Y = _moveAccumulatedY;
    }

    private void MoveThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var movedX = _moveAccumulatedX;
        var movedY = _moveAccumulatedY;
        DragPreviewTransform.X = 0;
        DragPreviewTransform.Y = 0;

        if (DataContext is not DashboardCardViewModel card)
        {
            return;
        }

        var (cellWidth, cellHeight) = GetCellSize(card);
        if (cellWidth <= 0 || cellHeight <= 0)
        {
            return;
        }

        var deltaColumns = (int)Math.Round(movedX / cellWidth, MidpointRounding.AwayFromZero);
        var deltaRows = (int)Math.Round(movedY / cellHeight, MidpointRounding.AwayFromZero);
        if (deltaColumns == 0 && deltaRows == 0)
        {
            return;
        }

        // TryMove itself clamps to the grid bounds and snaps back to the original cell (a no-op
        // here, since we never modified Row/Column ourselves) if the target is occupied.
        card.TryMove(card.Row + deltaRows, card.Column + deltaColumns);
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resizeAccumulatedX = 0;
        _resizeAccumulatedY = 0;
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        if (DataContext is DashboardCardViewModel card)
        {
            (_resizeCellWidth, _resizeCellHeight) = GetCellSize(card);
            _resizePreviewRowSpan = card.RowSpan;
            _resizePreviewColumnSpan = card.ColumnSpan;
        }
        else
        {
            _resizeCellWidth = 0;
            _resizeCellHeight = 0;
            _resizePreviewRowSpan = 1;
            _resizePreviewColumnSpan = 1;
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _resizeAccumulatedX += e.HorizontalChange;
        _resizeAccumulatedY += e.VerticalChange;
        if (DataContext is not DashboardCardViewModel card || _resizeCellWidth <= 0 || _resizeCellHeight <= 0)
        {
            return;
        }

        var (maxRowSpan, maxColumnSpan) = GetMaximumSpans(card);
        _resizePreviewColumnSpan = Math.Clamp(
            (int)Math.Round((_resizeStartWidth + _resizeAccumulatedX + Margin.Left + Margin.Right) / _resizeCellWidth,
                MidpointRounding.AwayFromZero),
            card.MinColumnSpan,
            maxColumnSpan);
        _resizePreviewRowSpan = Math.Clamp(
            (int)Math.Round((_resizeStartHeight + _resizeAccumulatedY + Margin.Top + Margin.Bottom) / _resizeCellHeight,
                MidpointRounding.AwayFromZero),
            card.MinRowSpan,
            maxRowSpan);

        // Preview with an actual Grid span, rather than an overflowing explicit Width/Height.
        // That lets WPF lay out the complete card immediately as each cell boundary is crossed.
        Grid.SetColumnSpan(this, _resizePreviewColumnSpan);
        Grid.SetRowSpan(this, _resizePreviewRowSpan);
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is not DashboardCardViewModel card)
        {
            return;
        }

        // Restore the committed span first. TryResize either publishes the preview span through
        // the view model or leaves the original span in place when another card occupies it.
        Grid.SetColumnSpan(this, card.ColumnSpan);
        Grid.SetRowSpan(this, card.RowSpan);
        card.TryResize(_resizePreviewRowSpan, _resizePreviewColumnSpan);
    }

    // A grid cell's pixel size, derived from this control's own current render size divided by
    // how many cells it currently spans - correct regardless of the dashboard grid's overall
    // pixel size or how many rows/columns it currently has, since every cell in the grid is an
    // equal "*"-sized share of the same Grid.
    private (double CellWidth, double CellHeight) GetCellSize(DashboardCardViewModel card)
    {
        var totalWidth = ActualWidth + Margin.Left + Margin.Right;
        var totalHeight = ActualHeight + Margin.Top + Margin.Bottom;
        var cellWidth = card.ColumnSpan > 0 ? totalWidth / card.ColumnSpan : 0;
        var cellHeight = card.RowSpan > 0 ? totalHeight / card.RowSpan : 0;
        return (cellWidth, cellHeight);
    }

    private (int MaxRowSpan, int MaxColumnSpan) GetMaximumSpans(DashboardCardViewModel card)
    {
        var dashboardGrid = VisualTreeHelper.GetParent(this) as Grid;
        var maxRowSpan = Math.Max(card.MinRowSpan, (dashboardGrid?.RowDefinitions.Count ?? card.Row + card.RowSpan) - card.Row);
        var maxColumnSpan = Math.Max(card.MinColumnSpan, (dashboardGrid?.ColumnDefinitions.Count ?? card.Column + card.ColumnSpan) - card.Column);
        return (maxRowSpan, maxColumnSpan);
    }
}
