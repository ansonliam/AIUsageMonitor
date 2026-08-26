using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Models;

public sealed class TaskbarMonitorOption(
    string id,
    string displayName,
    bool isEnabled,
    double textSize,
    double iconSize,
    double textVerticalOffset,
    double leftOffset,
    double rightOffset,
    string alignment,
    bool hasTrayIcons,
    Action<string, bool> onEnabledChanged,
    Action<string, double, double, double, double, double, string> onAppearanceChanged)
    : ObservableObject
{
    private bool _isEnabled = isEnabled;
    private double _textSize = textSize;
    private double _iconSize = iconSize;
    private double _textVerticalOffset = textVerticalOffset;
    // Stored separately - left and right alignment default to different offsets (0 vs the
    // original flush-with-tray-ish spacing), so switching alignment must not carry one over as
    // the other's value.
    private double _leftOffset = leftOffset;
    private double _rightOffset = rightOffset;
    private string _displayName = displayName;
    private string _alignment = alignment;
    private bool _hasTrayIcons = hasTrayIcons;

    public string Id { get; } = id;
    public string DisplayName => _displayName;
    public bool HasTrayIcons => _hasTrayIcons;

    // Raw per-side values, exposed so RefreshTaskbarMonitors can carry both across into an
    // existing option's ApplyState without losing whichever side isn't currently active - the
    // bindable OffsetPx only ever exposes the active side.
    public double LeftOffsetRaw => _leftOffset;
    public double RightOffsetRaw => _rightOffset;

    // The offset field is meaningless when the widget is flush against the tray icon cluster
    // (right-aligned on the monitor that owns the tray) - that position is anchored to the tray,
    // not to an offset. Every other combination (left-aligned anywhere, right-aligned on a
    // secondary monitor) does use it.
    public bool ShowOffsetField => _alignment == "Left" || !_hasTrayIcons;

    // Binary choice, driven by a segmented toggle in the UI rather than a checkbox/dropdown.
    public bool AlignRight
    {
        get => _alignment != "Left";
        set => Alignment = value ? "Right" : "Left";
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                onEnabledChanged(Id, value);
            }
        }
    }

    public double TextSize
    {
        get => _textSize;
        set
        {
            if (SetProperty(ref _textSize, value))
            {
                RaiseAppearanceChanged();
            }
        }
    }

    public double IconSize
    {
        get => _iconSize;
        set
        {
            if (SetProperty(ref _iconSize, value))
            {
                RaiseAppearanceChanged();
            }
        }
    }

    public double TextVerticalOffset
    {
        get => _textVerticalOffset;
        set
        {
            if (SetProperty(ref _textVerticalOffset, value))
            {
                RaiseAppearanceChanged();
            }
        }
    }

    // Single field the UI binds to - reads/writes whichever backing offset matches the current
    // alignment, so the box always shows that side's own remembered value.
    public double OffsetPx
    {
        get => AlignRight ? _rightOffset : _leftOffset;
        set
        {
            var changed = AlignRight
                ? SetProperty(ref _rightOffset, value, nameof(OffsetPx))
                : SetProperty(ref _leftOffset, value, nameof(OffsetPx));
            if (changed)
            {
                RaiseAppearanceChanged();
            }
        }
    }

    // "Left" anchors the widget to the left edge of this monitor's taskbar (offset from there);
    // "Right" preserves the original behaviour - flush against the tray icon cluster on the
    // monitor that owns the tray, or offset in from the right edge on a secondary monitor.
    public string Alignment
    {
        get => _alignment;
        set
        {
            if (SetProperty(ref _alignment, value))
            {
                OnPropertyChanged(nameof(ShowOffsetField));
                OnPropertyChanged(nameof(AlignRight));
                OnPropertyChanged(nameof(OffsetPx));
                RaiseAppearanceChanged();
            }
        }
    }

    private void RaiseAppearanceChanged() =>
        onAppearanceChanged(Id, _textSize, _iconSize, _textVerticalOffset, _leftOffset, _rightOffset, _alignment);

    public void ApplyAppearance(double textSize, double iconSize, double textVerticalOffset, double leftOffset, double rightOffset, string alignment)
    {
        SetProperty(ref _textSize, textSize, nameof(TextSize));
        SetProperty(ref _iconSize, iconSize, nameof(IconSize));
        SetProperty(ref _textVerticalOffset, textVerticalOffset, nameof(TextVerticalOffset));
        SetProperty(ref _leftOffset, leftOffset, nameof(OffsetPx));
        SetProperty(ref _rightOffset, rightOffset, nameof(OffsetPx));
        OnPropertyChanged(nameof(OffsetPx));
        if (SetProperty(ref _alignment, alignment, nameof(Alignment)))
        {
            OnPropertyChanged(nameof(ShowOffsetField));
            OnPropertyChanged(nameof(AlignRight));
        }
    }

    public void ApplyState(
        string displayName,
        bool hasTrayIcons,
        bool isEnabled,
        double textSize,
        double iconSize,
        double textVerticalOffset,
        double leftOffset,
        double rightOffset,
        string alignment)
    {
        SetProperty(ref _displayName, displayName, nameof(DisplayName));
        if (SetProperty(ref _hasTrayIcons, hasTrayIcons, nameof(HasTrayIcons)))
        {
            OnPropertyChanged(nameof(ShowOffsetField));
        }
        SetProperty(ref _isEnabled, isEnabled, nameof(IsEnabled));
        ApplyAppearance(textSize, iconSize, textVerticalOffset, leftOffset, rightOffset, alignment);
    }
}
