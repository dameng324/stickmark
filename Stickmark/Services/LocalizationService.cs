using System.Globalization;
using System.Threading;

namespace Stickmark.Services;

public static class LocalizationService
{
    /// Applies the current thread/default culture. Stickmark currently ships English resources only.
    /// Must be called before creating a window when starting the app.
    public static void ApplyCulture(string? language)
    {
        // Stickmark ships English UI resources only. Unknown/system cultures fall back to English.
        var culture = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? new CultureInfo("en")
            : CultureInfo.CurrentUICulture;

        if (!string.Equals(culture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase))
            culture = new CultureInfo("en");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
    }
}
