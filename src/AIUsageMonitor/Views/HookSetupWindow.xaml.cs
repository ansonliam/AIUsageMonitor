using System.Windows;

namespace AIUsageMonitor.Views;

public sealed class HookSetupOption
{
    public HookSetupOption(string key, string displayName, bool needsRepair)
    {
        Key = key;
        DisplayName = needsRepair ? $"{displayName} (repair invalid hook)" : displayName;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public bool IsSelected { get; set; } = true;
}

public partial class HookSetupWindow : Window
{
    public HookSetupWindow(IReadOnlyList<HookSetupOption> options)
    {
        InitializeComponent();
        Options = options;
        DataContext = this;
    }

    public IReadOnlyList<HookSetupOption> Options { get; }

    public IReadOnlyCollection<string> SelectedKeys => Options
        .Where(option => option.IsSelected)
        .Select(option => option.Key)
        .ToArray();

    private void SetUp_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
