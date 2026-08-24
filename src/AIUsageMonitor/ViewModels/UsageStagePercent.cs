using System.Globalization;

namespace AIUsageMonitor.ViewModels;

// Stage thresholds are always stored and validated as "used%" (see UsageColorConverter). This
// converts between that canonical scale and whatever the user has chosen to see in the Settings
// textboxes - "used" (identity) or "remaining" (100 - used). Flipping "used%" is self-inverse, so
// the same formula converts both ways.
//
// Deliberately free of any WPF dependency: SettingsViewModel can only be constructed with real
// MainWindow/TaskbarWidgetWindow instances, so keeping the whole textbox format/parse/validate
// path here is what makes it unit-testable.
internal static class UsageStagePercent
{
    public static double ToDisplay(double usedPercent, bool showRemaining) =>
        showRemaining ? 100 - usedPercent : usedPercent;

    public static double ToUsed(double displayPercent, bool showRemaining) =>
        showRemaining ? 100 - displayPercent : displayPercent;

    // Renders a stored used% into a stage textbox on the scale currently being displayed.
    public static string Format(double usedPercent, bool showRemaining) =>
        ToDisplay(usedPercent, showRemaining).ToString("0.##", CultureInfo.InvariantCulture);

    // Reads what the user typed on the displayed scale back into the stored used% scale.
    public static bool TryParse(string text, bool showRemaining, out double usedPercent)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var displayValue))
        {
            usedPercent = default;
            return false;
        }

        usedPercent = ToUsed(displayValue, showRemaining);
        return true;
    }

    // Stage order is validated on the used% scale, so in remaining mode the user has to enter
    // percentages that count *down* - saying "increasing" there would send them the wrong way.
    public static string ValidationMessage(bool showRemaining) => showRemaining
        ? "Use valid HEX colours and four decreasing percentages from 100 to 0."
        : "Use valid HEX colours and four increasing percentages from 0 to 100.";

    // Five colour buckets need only four cut points: the last stage is whatever sits beyond
    // stage 4, open-ended in both directions of the scale. It has no maximum to type, so its row
    // is rendered from stage 4's boundary instead of being an input. Both readings are strict -
    // stage 4 owns its own boundary value (used <= stage4Max), so stage 5 starts just past it.
    public static string OpenEndedStageText(double stage4UsedMaximum, bool showRemaining) =>
        showRemaining
            ? $"below {Format(stage4UsedMaximum, showRemaining: true)}%"
            : $"above {Format(stage4UsedMaximum, showRemaining: false)}%";
}
