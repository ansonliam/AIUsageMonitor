namespace AIUsageMonitor.ViewModels;

// One card on the opt-in free-form dashboard grid. "Content" is whatever the card displays -
// a ProviderViewModel (Codex/Claude/Antigravity/Cursor usage) or a CodexApiCostPanelViewModel
// (a Codex/Azure OpenAI or Claude/AWS Bedrock endpoint cost panel). WPF's implicit DataTemplate
// lookup already renders both types correctly wherever a ContentPresenter binds to Content - see
// the DataTemplates in MainWindow.xaml.Resources - so this class deliberately does not know or
// care which kind of content it holds.
//
// Row/Column/RowSpan/ColumnSpan only have internal setters: every move or resize must go through
// the owning DashboardLayoutViewModel so collision checks and persistence stay correct. Views
// bind to these properties read-only and call TryMove/TryResize to request a change.
public sealed class DashboardCardViewModel : ObservableObject
{
    private readonly DashboardLayoutViewModel _owner;
    private int _row;
    private int _column;
    private int _rowSpan;
    private int _columnSpan;
    private bool _isEditMode;

    internal DashboardCardViewModel(
        DashboardLayoutViewModel owner,
        string id,
        object content,
        int minRowSpan,
        int minColumnSpan)
    {
        _owner = owner;
        Id = id;
        Content = content;
        MinRowSpan = minRowSpan;
        MinColumnSpan = minColumnSpan;
    }

    public string Id { get; }
    public object Content { get; }
    public int MinRowSpan { get; }
    public int MinColumnSpan { get; }

    public int Row { get => _row; internal set => SetProperty(ref _row, value); }
    public int Column { get => _column; internal set => SetProperty(ref _column, value); }
    public int RowSpan { get => _rowSpan; internal set => SetProperty(ref _rowSpan, value); }
    public int ColumnSpan { get => _columnSpan; internal set => SetProperty(ref _columnSpan, value); }

    // Mirrors DashboardLayoutViewModel.IsEditMode onto every card so DashboardCard's drag/resize
    // overlay (bound to this, since the card's DataContext in the ItemsControl is this view
    // model) can show/hide without any RelativeSource lookup back up through the ItemsControl.
    public bool IsEditMode { get => _isEditMode; internal set => SetProperty(ref _isEditMode, value); }

    public bool TryMove(int row, int column) => _owner.TryMoveCard(this, row, column);

    public bool TryResize(int rowSpan, int columnSpan) => _owner.TryResizeCard(this, rowSpan, columnSpan);
}
