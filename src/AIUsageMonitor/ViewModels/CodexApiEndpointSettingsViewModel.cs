using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor.ViewModels;

public sealed class CodexModelPricingViewModel : ObservableObject
{
    private string _inputText = "";
    private string _cachedInputText = "";
    private string _cacheWriteText = "";
    private string _outputText = "";

    public CodexModelPricingViewModel(string model, string displayName, ApiEndpointType endpointType)
    {
        Model = model;
        DisplayName = displayName;
        DefaultHintText = BuildDefaultHint(model, endpointType);
        ResetToDefaultCommand = new RelayCommand(ResetToDefault);
    }

    public string Model { get; }
    public string DisplayName { get; }
    public string InputText { get => _inputText; set => SetProperty(ref _inputText, value); }
    public string CachedInputText { get => _cachedInputText; set => SetProperty(ref _cachedInputText, value); }
    public string CacheWriteText { get => _cacheWriteText; set => SetProperty(ref _cacheWriteText, value); }
    public string OutputText { get => _outputText; set => SetProperty(ref _outputText, value); }
    public string DefaultHintText { get; }
    public ICommand ResetToDefaultCommand { get; }

    public void FromOverride(ModelPricingOverride? pricing)
    {
        InputText = FormatDecimal(pricing?.InputPerMillion);
        CachedInputText = FormatDecimal(pricing?.CachedInputPerMillion);
        CacheWriteText = FormatDecimal(pricing?.CacheWritePerMillion);
        OutputText = FormatDecimal(pricing?.OutputPerMillion);
    }

    public void ResetToDefault()
    {
        InputText = "";
        CachedInputText = "";
        CacheWriteText = "";
        OutputText = "";
    }

    public ModelPricingOverride ToOverride() => new()
    {
        InputPerMillion = ParseDecimal(InputText),
        CachedInputPerMillion = ParseDecimal(CachedInputText),
        CacheWritePerMillion = ParseDecimal(CacheWriteText),
        OutputPerMillion = ParseDecimal(OutputText)
    };

    private static string FormatDecimal(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static decimal? ParseDecimal(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string BuildDefaultHint(string model, ApiEndpointType endpointType)
    {
        var pricing = endpointType == ApiEndpointType.CodexAzureOpenAI
            ? CodexPricingRegistry.GetDefault(model)
            : ClaudePricingRegistry.GetDefault(model);
        return pricing is { IsComplete: true }
            ? $"Default AUD: {pricing.InputPerMillion:0.0000} / {pricing.CachedInputPerMillion:0.0000} / {pricing.CacheWritePerMillion:0.0000} / {pricing.OutputPerMillion:0.0000}"
            : "AUD pricing required";
    }

}

public sealed class CodexApiEndpointSettingsViewModel : ObservableObject
{
    // Fixed options for the Type combo box, following this codebase's existing
    // ItemsSource/SelectedItem-on-a-plain-string convention (see e.g. SettingsViewModel's
    // FontSizePresets/WidgetFonts) rather than binding directly to the enum.
    public static readonly string[] TypeOptions = ["Azure OpenAI (Codex)", "AWS Bedrock (Claude)"];
    private const string ClaudeTypeOption = "AWS Bedrock (Claude)";
    private const string CodexTypeOption = "Azure OpenAI (Codex)";

    private string _name;
    private string _endpoint;
    private string _trackFromText;
    private string _monthlyBudgetText;
    private string _manualCostAdjustmentText;
    private bool _showInWidget;
    private string _cacheHitRateText;
    private string _awsRegion;
    private ApiEndpointType _type;

    // Pricing values entered for a model are kept here across Type switches so toggling the Type
    // combo box back and forth never silently discards anything the user already typed in - see
    // RebuildPricingRows.
    private readonly Dictionary<string, ModelPricingOverride> _pricingOverridesByModel;

    public CodexApiEndpointSettingsViewModel(
        CodexApiEndpointSettings settings,
        Action<CodexApiEndpointSettingsViewModel> onDelete)
    {
        Id = settings.Id;
        _type = settings.Type;
        _name = settings.Name;
        _endpoint = settings.Endpoint;
        _trackFromText = settings.TrackFrom == default
            ? DateTimeOffset.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            : settings.TrackFrom.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        _monthlyBudgetText = settings.MonthlyBudget?.ToString(CultureInfo.InvariantCulture) ?? "";
        _manualCostAdjustmentText = settings.ManualCostAdjustment == 0
            ? ""
            : settings.ManualCostAdjustment.ToString(CultureInfo.InvariantCulture);
        _showInWidget = settings.ShowInWidget;
        _cacheHitRateText = settings.CacheHitRatePercent?.ToString(CultureInfo.InvariantCulture) ?? "";
        _awsRegion = settings.AwsRegion;

        _pricingOverridesByModel = new Dictionary<string, ModelPricingOverride>(settings.PricingOverrides);
        PricingRows = [];
        RebuildPricingRows();

        DeleteCommand = new RelayCommand(() => onDelete(this));
    }

    public Guid Id { get; }

    public ApiEndpointType Type
    {
        get => _type;
        set
        {
            if (_type == value)
            {
                return;
            }

            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(IsCodexType));
                OnPropertyChanged(nameof(IsClaudeType));
                OnPropertyChanged(nameof(TypeDisplayText));
                OnPropertyChanged(nameof(PricingHelpText));
                RebuildPricingRows();
            }
        }
    }

    // Bound to the Type combo box's SelectedItem - see TypeOptions.
    public string TypeDisplayText
    {
        get => Type == ApiEndpointType.ClaudeAwsBedrock ? ClaudeTypeOption : CodexTypeOption;
        set => Type = value == ClaudeTypeOption ? ApiEndpointType.ClaudeAwsBedrock : ApiEndpointType.CodexAzureOpenAI;
    }

    public bool IsCodexType => Type == ApiEndpointType.CodexAzureOpenAI;
    public bool IsClaudeType => Type == ApiEndpointType.ClaudeAwsBedrock;
    public string PricingHelpText => IsCodexType
        ? "Enter the AUD prices charged by this Azure OpenAI resource per 1 million tokens. These prices apply only to this Azure endpoint."
        : "Enter the AUD prices charged by this AWS Bedrock account per 1 million tokens. Cache Write is the normal 5-minute rate; one-hour cache writes are not separately modeled yet.";

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Endpoint { get => _endpoint; set => SetProperty(ref _endpoint, value); }
    public string TrackFromText { get => _trackFromText; set => SetProperty(ref _trackFromText, value); }
    public string MonthlyBudgetText { get => _monthlyBudgetText; set => SetProperty(ref _monthlyBudgetText, value); }
    public string ManualCostAdjustmentText { get => _manualCostAdjustmentText; set => SetProperty(ref _manualCostAdjustmentText, value); }
    public bool ShowInWidget { get => _showInWidget; set => SetProperty(ref _showInWidget, value); }
    public string CacheHitRateText { get => _cacheHitRateText; set => SetProperty(ref _cacheHitRateText, value); }
    public string AwsRegion { get => _awsRegion; set => SetProperty(ref _awsRegion, value); }
    public ObservableCollection<CodexModelPricingViewModel> PricingRows { get; private set; }
    public ICommand DeleteCommand { get; }

    // Rebuilds PricingRows for whichever endpoint Type is now selected. Any values currently shown
    // are saved back into _pricingOverridesByModel first, so switching Type and switching back
    // reproduces exactly what the user had entered, for both sides.
    private void RebuildPricingRows()
    {
        foreach (var row in PricingRows)
        {
            _pricingOverridesByModel[row.Model] = row.ToOverride();
        }

        var models = Type == ApiEndpointType.ClaudeAwsBedrock
            ? ClaudePricingRegistry.KnownModels
            : CodexPricingRegistry.KnownModels;

        var rows = new ObservableCollection<CodexModelPricingViewModel>(
            models.Select(model => new CodexModelPricingViewModel(model, ToDisplayName(model), Type)));
        foreach (var row in rows)
        {
            row.FromOverride(_pricingOverridesByModel.GetValueOrDefault(row.Model));
        }

        PricingRows = rows;
        OnPropertyChanged(nameof(PricingRows));
    }

    public bool TryToSettings(out CodexApiEndpointSettings settings, out string? validationError)
    {
        settings = null!;
        if (string.IsNullOrWhiteSpace(Name))
        {
            validationError = "Each endpoint needs a name.";
            return false;
        }

        var normalizedHost = "";
        if (Type == ApiEndpointType.CodexAzureOpenAI)
        {
            if (!CodexEndpointNormalizer.TryNormalizeHost(Endpoint, out normalizedHost))
            {
                validationError = $"\"{Endpoint}\" is not a valid Codex endpoint.";
                return false;
            }
        }

        // Must match the display format exactly (dd/MM/yyyy, day-first) - a culture-driven parse
        // (e.g. invariant/US month-first) rejects any day above 12, which is most days of the month.
        if (!DateTimeOffset.TryParseExact(
                TrackFromText.Trim(),
                "d/M/yyyy H:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var trackFrom))
        {
            validationError = $"\"{TrackFromText}\" is not a valid Track From date/time. Use dd/MM/yyyy HH:mm, e.g. 22/08/2026 04:00.";
            return false;
        }

        decimal? monthlyBudget = null;
        if (!string.IsNullOrWhiteSpace(MonthlyBudgetText))
        {
            if (!decimal.TryParse(MonthlyBudgetText, NumberStyles.Number, CultureInfo.InvariantCulture, out var budget) || budget < 0)
            {
                validationError = $"\"{MonthlyBudgetText}\" is not a valid Monthly Budget.";
                return false;
            }

            monthlyBudget = budget;
        }

        decimal? cacheHitRatePercent = null;
        if (Type == ApiEndpointType.CodexAzureOpenAI && !string.IsNullOrWhiteSpace(CacheHitRateText))
        {
            if (!decimal.TryParse(CacheHitRateText, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) ||
                rate < 0 || rate > 100)
            {
                validationError = $"\"{CacheHitRateText}\" is not a valid Cache Match Rate (0-100).";
                return false;
            }

            cacheHitRatePercent = rate;
        }

        decimal manualCostAdjustment = 0;
        if (!string.IsNullOrWhiteSpace(ManualCostAdjustmentText) &&
            !decimal.TryParse(ManualCostAdjustmentText, NumberStyles.Number, CultureInfo.InvariantCulture, out manualCostAdjustment))
        {
            validationError = $"\"{ManualCostAdjustmentText}\" is not a valid AUD adjustment.";
            return false;
        }

        // Save whatever's currently displayed back into _pricingOverridesByModel first, so an
        // endpoint saved right after switching Type doesn't lose the values for whichever side
        // isn't currently showing.
        foreach (var row in PricingRows)
        {
            _pricingOverridesByModel[row.Model] = row.ToOverride();
        }

        settings = new CodexApiEndpointSettings
        {
            Id = Id,
            Type = Type,
            Name = Name.Trim(),
            Endpoint = Type == ApiEndpointType.CodexAzureOpenAI ? Endpoint.Trim() : "",
            NormalizedHost = normalizedHost,
            TrackFrom = trackFrom,
            MonthlyBudget = monthlyBudget,
            ManualCostAdjustment = manualCostAdjustment,
            PricingOverrides = new Dictionary<string, ModelPricingOverride>(_pricingOverridesByModel),
            ShowInWidget = ShowInWidget,
            CacheHitRatePercent = cacheHitRatePercent,
            AwsRegion = Type == ApiEndpointType.ClaudeAwsBedrock ? AwsRegion.Trim() : ""
        };
        validationError = null;
        return true;
    }

    private static string ToDisplayName(string model) => model switch
    {
        "gpt-5.6-sol" => "GPT-5.6 Sol",
        "gpt-5.6-terra" => "GPT-5.6 Terra",
        "gpt-5.6-luna" => "GPT-5.6 Luna",
        "claude-opus" => "Claude Opus",
        "claude-sonnet" => "Claude Sonnet",
        "claude-haiku" => "Claude Haiku",
        "claude-other" => "Claude (other detected)",
        _ => model
    };
}
