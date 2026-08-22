using System.Collections.ObjectModel;
using System.Windows.Input;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.ViewModels;

// Owns the opt-in free-form "Dashboard Layout" grid: a 6-column-wide board that Providers usage
// cards (Codex/Claude/Antigravity/Cursor) and per-endpoint API cost cards (Codex/Azure OpenAI or
// Claude/AWS Bedrock - see CodexApiCostPanelViewModel) can be dragged and resized on.
//
// This does not replace the normal compact auto-sized widget view - MainWindow only renders this
// grid when the user explicitly turns Dashboard Layout on. Card *visibility* is still controlled
// entirely by the existing mechanisms (MainWindow's ShowCodex/ShowClaude/etc. and each API cost
// endpoint's own ShowInWidget setting): a card simply does not exist in Cards while its backing
// provider/endpoint is hidden, and reappears in its last saved position when shown again.
public sealed class DashboardLayoutViewModel : ObservableObject
{
    // Safety cap on how far Rows can auto-grow (see FindFreeSlot) - protects against an
    // unbounded loop if something is pathologically wrong with the saved/live card set.
    private const int MaxRows = 40;

    private readonly MainViewModel _mainViewModel;
    private readonly DashboardLayoutStore _store;

    // Remembers every card's last known position/size, including ones for a currently-hidden
    // provider or endpoint, so toggling something back on restores where it used to be instead
    // of dropping it into whatever slot happens to be free at that moment.
    private readonly Dictionary<string, DashboardLayoutItem> _savedPositions;

    private int _rows;
    private bool _isEditMode;

    public DashboardLayoutViewModel(MainViewModel mainViewModel, DashboardLayoutStore store)
    {
        _mainViewModel = mainViewModel;
        _store = store;
        Cards = [];

        var saved = _store.Load();
        Columns = saved.Columns > 0 ? saved.Columns : 6;
        _rows = saved.Rows >= 6 ? saved.Rows : 6;
        _savedPositions = saved.Items.ToDictionary(item => item.Id);

        _mainViewModel.Providers.CollectionChanged += (_, _) => SyncCards();
        _mainViewModel.CodexApiCostPanels.CollectionChanged += (_, _) => SyncCards();
        SyncCards();
        CompactEmptyRows();
        SaveLayout();

        ToggleEditModeCommand = new RelayCommand(() => IsEditMode = !IsEditMode);
        ResetLayoutCommand = new RelayCommand(ResetLayout);
    }

    public ObservableCollection<DashboardCardViewModel> Cards { get; }

    // Fixed for now - the editor UI always presents a 6-wide board. Rows is the variable
    // dimension: it starts at 6 and grows in increments of 2 (see FindFreeSlot) if more cards
    // are added than currently fit, so users with several API cost endpoints configured don't
    // lose cards off the bottom of a hard-capped grid.
    public int Columns { get; }
    public int Rows { get => _rows; private set => SetProperty(ref _rows, value); }

    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (!SetProperty(ref _isEditMode, value))
            {
                return;
            }

            foreach (var card in Cards)
            {
                card.IsEditMode = value;
            }

            if (value)
            {
                EnsureEditableSpace();
                SaveLayout();
            }
            else
            {
                // A user may intentionally leave a card in the lower parking rows. Do not pack
                // the layout on Done, otherwise its former empty row is removed and the card is
                // silently pulled back to where it started.
                SaveLayout();
            }
        }
    }

    public ICommand ToggleEditModeCommand { get; }
    public ICommand ResetLayoutCommand { get; }

    // Called by DashboardCardViewModel.TryMove, which is called by DashboardCard's drag Thumb on
    // DragCompleted (never on every intermediate DragDelta - see that control for why). Returns
    // false if the requested cell is occupied, so the view can snap the card back to where it was.
    public bool TryMoveCard(DashboardCardViewModel card, int row, int column)
    {
        if (row + card.RowSpan > Rows)
        {
            EnsureRowCapacity(Math.Min(MaxRows, row + card.RowSpan + 2));
        }

        row = Math.Clamp(row, 0, Math.Max(0, Rows - card.RowSpan));
        column = Math.Clamp(column, 0, Math.Max(0, Columns - card.ColumnSpan));
        if (row == card.Row && column == card.Column)
        {
            return true;
        }

        var conflicts = Cards
            .Where(other => !ReferenceEquals(other, card) &&
                            RectanglesOverlap(row, column, card.RowSpan, card.ColumnSpan,
                                other.Row, other.Column, other.RowSpan, other.ColumnSpan))
            .ToList();

        if (conflicts.Count > 0)
        {
            // A packed dashboard must still let the user reorder cards. Dropping directly onto
            // another card with the same shape swaps them; every card keeps its own dimensions.
            // Other collisions remain invalid rather than silently resizing or shuffling cards.
            if (conflicts.Count != 1 ||
                row != conflicts[0].Row ||
                column != conflicts[0].Column ||
                card.RowSpan != conflicts[0].RowSpan ||
                card.ColumnSpan != conflicts[0].ColumnSpan)
            {
                return false;
            }

            conflicts[0].Row = card.Row;
            conflicts[0].Column = card.Column;
        }

        card.Row = row;
        card.Column = column;
        if (!IsEditMode)
        {
            CompactEmptyRows();
        }
        SaveLayout();
        return true;
    }

    public bool TryResizeCard(DashboardCardViewModel card, int rowSpan, int columnSpan)
    {
        rowSpan = Math.Clamp(rowSpan, card.MinRowSpan, Math.Max(card.MinRowSpan, Rows - card.Row));
        columnSpan = Math.Clamp(columnSpan, card.MinColumnSpan, Math.Max(card.MinColumnSpan, Columns - card.Column));
        if (rowSpan == card.RowSpan && columnSpan == card.ColumnSpan)
        {
            return true;
        }

        if (Overlaps(card.Row, card.Column, rowSpan, columnSpan, card))
        {
            return false;
        }

        card.RowSpan = rowSpan;
        card.ColumnSpan = columnSpan;
        if (!IsEditMode)
        {
            CompactEmptyRows();
        }
        SaveLayout();
        return true;
    }

    private void ResetLayout()
    {
        _store.Delete();
        _savedPositions.Clear();
        Rows = 6;

        foreach (var card in Cards.ToList())
        {
            Cards.Remove(card);
        }

        SyncCards();
    }

    // Keep two entirely empty rows beneath the lowest card while editing. A spare half-width
    // cell elsewhere cannot hold a full-width cost card or let another card move aside first.
    private void EnsureEditableSpace()
    {
        if (Cards.Count == 0 || Rows >= MaxRows)
        {
            return;
        }

        var lowestOccupiedRow = Cards.Max(card => card.Row + card.RowSpan);
        var requiredRows = Math.Min(MaxRows, lowestOccupiedRow + 2);
        if (Rows < requiredRows)
        {
            Rows = requiredRows;
        }
    }

    // Adds a card for every currently-visible provider/endpoint that doesn't have one yet, and
    // removes cards for ones that disappeared (hidden, or an API cost endpoint that was deleted).
    // Run on startup and every time Providers or CodexApiCostPanels changes.
    private void SyncCards()
    {
        var desiredIds = new HashSet<string>();

        foreach (var provider in _mainViewModel.Providers)
        {
            var id = ProviderCardId(provider.Kind);
            desiredIds.Add(id);
            EnsureCard(id, provider, minRowSpan: 1, minColumnSpan: 2, defaultRowSpan: 2, defaultColumnSpan: 3);
        }

        foreach (var panel in _mainViewModel.CodexApiCostPanels)
        {
            var id = CostPanelCardId(panel.EndpointId);
            desiredIds.Add(id);
            EnsureCard(id, panel, minRowSpan: 1, minColumnSpan: 3, defaultRowSpan: 2, defaultColumnSpan: 6);
        }

        foreach (var stale in Cards.Where(existing => !desiredIds.Contains(existing.Id)).ToList())
        {
            Cards.Remove(stale);
        }

        if (IsEditMode)
        {
            EnsureEditableSpace();
            SaveLayout();
        }
    }

    private void EnsureCard(
        string id,
        object content,
        int minRowSpan,
        int minColumnSpan,
        int defaultRowSpan,
        int defaultColumnSpan)
    {
        if (Cards.Any(existing => existing.Id == id))
        {
            return;
        }

        var card = new DashboardCardViewModel(this, id, content, minRowSpan, minColumnSpan)
        {
            IsEditMode = IsEditMode
        };

        if (_savedPositions.TryGetValue(id, out var saved))
        {
            var rowSpan = Math.Max(minRowSpan, saved.RowSpan);
            var columnSpan = Math.Max(minColumnSpan, Math.Min(Columns, saved.ColumnSpan));
            EnsureRowCapacity(saved.Row + rowSpan);

            var row = Math.Clamp(saved.Row, 0, Math.Max(0, Rows - rowSpan));
            var column = Math.Clamp(saved.Column, 0, Math.Max(0, Columns - columnSpan));

            // Saved data should never conflict (nothing else has been placed for this id yet by
            // definition), but a hand-edited or corrupted layout file could still overlap another
            // card - fall back to an automatic slot rather than rendering two cards on top of
            // each other.
            if (Overlaps(row, column, rowSpan, columnSpan, ignoring: null))
            {
                (row, column) = FindFreeSlot(rowSpan, columnSpan);
            }

            card.RowSpan = rowSpan;
            card.ColumnSpan = columnSpan;
            card.Row = row;
            card.Column = column;
        }
        else
        {
            card.RowSpan = Math.Max(minRowSpan, Math.Min(defaultRowSpan, Rows));
            card.ColumnSpan = Math.Max(minColumnSpan, Math.Min(defaultColumnSpan, Columns));
            var (row, column) = FindFreeSlot(card.RowSpan, card.ColumnSpan);
            card.Row = row;
            card.Column = column;
        }

        Cards.Add(card);
        SaveLayout();
    }

    private (int Row, int Column) FindFreeSlot(int rowSpan, int columnSpan)
    {
        while (true)
        {
            for (var row = 0; row <= Rows - rowSpan; row++)
            {
                for (var column = 0; column <= Columns - columnSpan; column++)
                {
                    if (!Overlaps(row, column, rowSpan, columnSpan, ignoring: null))
                    {
                        return (row, column);
                    }
                }
            }

            if (Rows >= MaxRows)
            {
                // Grid is genuinely full even after growing to the safety cap - stack the new
                // card at the origin rather than throwing. Visually overlapping is recoverable
                // (the user can drag it away in Edit Layout); an exception is not.
                return (0, 0);
            }

            Rows += 2;
        }
    }

    private void EnsureRowCapacity(int requiredRows)
    {
        if (requiredRows > Rows)
        {
            Rows = Math.Min(MaxRows, requiredRows);
        }
    }

    private bool Overlaps(int row, int column, int rowSpan, int columnSpan, DashboardCardViewModel? ignoring)
    {
        foreach (var card in Cards)
        {
            if (ReferenceEquals(card, ignoring))
            {
                continue;
            }

            if (RectanglesOverlap(row, column, rowSpan, columnSpan,
                    card.Row, card.Column, card.RowSpan, card.ColumnSpan))
            {
                return true;
            }
        }

        return false;
    }

    // Remove only rows that no card occupies. This never changes a card's span or column, but
    // prevents the blank full-width bands seen after a card was moved through temporary space.
    private void CompactEmptyRows()
    {
        var usedRows = new bool[Rows];
        foreach (var card in Cards)
        {
            for (var row = card.Row; row < Math.Min(Rows, card.Row + card.RowSpan); row++)
            {
                usedRows[row] = true;
            }
        }

        var emptyRows = Enumerable.Range(0, Rows).Where(row => !usedRows[row]).ToList();
        if (emptyRows.Count == 0)
        {
            return;
        }

        foreach (var card in Cards)
        {
            card.Row -= emptyRows.Count(emptyRow => emptyRow < card.Row);
        }

        Rows = Math.Max(6, Rows - emptyRows.Count);
    }

    private static bool RectanglesOverlap(
        int firstRow,
        int firstColumn,
        int firstRowSpan,
        int firstColumnSpan,
        int secondRow,
        int secondColumn,
        int secondRowSpan,
        int secondColumnSpan) =>
        firstColumn < secondColumn + secondColumnSpan &&
        firstColumn + firstColumnSpan > secondColumn &&
        firstRow < secondRow + secondRowSpan &&
        firstRow + firstRowSpan > secondRow;

    private void SaveLayout()
    {
        var layout = new DashboardLayout
        {
            Columns = Columns,
            Rows = Rows,
            Items = Cards.Select(card => new DashboardLayoutItem
            {
                Id = card.Id,
                Row = card.Row,
                Column = card.Column,
                RowSpan = card.RowSpan,
                ColumnSpan = card.ColumnSpan
            }).ToList()
        };

        foreach (var item in layout.Items)
        {
            _savedPositions[item.Id] = item;
        }

        _store.Save(layout);
    }

    private static string ProviderCardId(ProviderKind kind) => $"provider:{kind}";

    private static string CostPanelCardId(Guid endpointId) => $"apiCost:{endpointId}";
}
