using AIUsageMonitor.ViewModels;

namespace AIUsageMonitor.Models;

public sealed class TaskbarMonitorOption(
    string id,
    string displayName,
    bool isEnabled,
    double textSize,
    double iconSize,
    double textVerticalOffset,
    double rightOffset,
    bool usesRightOffset,
    Action<string, bool> onEnabledChanged,
    Action<string, double, double, double, double> onAppearanceChanged)
    : ObservableObject
{
    private bool _isEnabled = isEnabled;
    private double _textSize = textSize;
    private double _iconSize = iconSize;
    private double _textVerticalOffset = textVerticalOffset;
    private double _rightOffset = rightOffset;
    private string _displayName = displayName;
    private bool _usesRightOffset = usesRightOffset;

    public string Id { get; } = id;
    public string DisplayName => _displayName;
    public bool UsesRightOffset => _usesRightOffset;

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
                onAppearanceChanged(Id, _textSize, _iconSize, _textVerticalOffset, _rightOffset);
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
                onAppearanceChanged(Id, _textSize, _iconSize, _textVerticalOffset, _rightOffset);
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
                onAppearanceChanged(Id, _textSize, _iconSize, _textVerticalOffset, _rightOffset);
            }
        }
    }

    public double RightOffset
    {
        get => _rightOffset;
        set
        {
            if (SetProperty(ref _rightOffset, value))
            {
                onAppearanceChanged(Id, _textSize, _iconSize, _textVerticalOffset, _rightOffset);
            }
        }
    }

    public void ApplyAppearance(double textSize, double iconSize, double textVerticalOffset, double rightOffset)
    {
        SetProperty(ref _textSize, textSize, nameof(TextSize));
        SetProperty(ref _iconSize, iconSize, nameof(IconSize));
        SetProperty(ref _textVerticalOffset, textVerticalOffset, nameof(TextVerticalOffset));
        SetProperty(ref _rightOffset, rightOffset, nameof(RightOffset));
    }

    public void ApplyState(
        string displayName,
        bool usesRightOffset,
        bool isEnabled,
        double textSize,
        double iconSize,
        double textVerticalOffset,
        double rightOffset)
    {
        SetProperty(ref _displayName, displayName, nameof(DisplayName));
        SetProperty(ref _usesRightOffset, usesRightOffset, nameof(UsesRightOffset));
        SetProperty(ref _isEnabled, isEnabled, nameof(IsEnabled));
        ApplyAppearance(textSize, iconSize, textVerticalOffset, rightOffset);
    }
}
