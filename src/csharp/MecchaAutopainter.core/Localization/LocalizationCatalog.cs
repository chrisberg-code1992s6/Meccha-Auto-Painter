using System.Reflection;
using System.Text.Json;

namespace MecchaCamouflage.Core;

public sealed record LocaleInfo(string Code, string NativeName);

public sealed class LocalizationCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> strings;

    private LocalizationCatalog(Dictionary<string, Dictionary<string, string>> strings)
    {
        this.strings = strings;
    }

    public static IReadOnlyList<LocaleInfo> SupportedLocales { get; } =
    [
        // Keep the default language first; sort the rest by native display name.
        new("en", "English"),
        new("id", "Bahasa Indonesia"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("pt-BR", "Português (Brasil)"),
        new("vi", "Tiếng Việt"),
        new("tr", "Türkçe"),
        new("ru", "Русский"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("zh-Hans", "简体中文"),
        new("zh-Hant", "繁體中文")
    ];

    public static bool IsSupported(string? locale) =>
        SupportedLocales.Any(item => string.Equals(item.Code, locale, StringComparison.OrdinalIgnoreCase));

    public static string DetectSystemLanguage()
    {
        var culture = System.Globalization.CultureInfo.CurrentUICulture;

        if (IsSupported(culture.Name))
            return culture.Name;

        if (IsSupported(culture.TwoLetterISOLanguageName))
            return culture.TwoLetterISOLanguageName;

        return "en";
    }

    public static LocalizationCatalog Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MecchaCamouflage.Core.Localization.Strings.json")
            ?? throw new InvalidOperationException("Localization resource not found.");
        var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidOperationException("Localization resource is invalid.");
        return new LocalizationCatalog(data);
    }

    public string Text(string locale, string key)
    {
        if (strings.TryGetValue(locale, out var localeStrings) && localeStrings.TryGetValue(key, out var localized))
            return localized;
        if (strings.TryGetValue("en", out var english) && english.TryGetValue(key, out var fallback))
            return fallback;
        return key;
    }

    public IReadOnlyDictionary<string, string> For(string locale) =>
        strings.TryGetValue(locale, out var localeStrings) ? localeStrings : strings["en"];

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> All =>
        strings.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value);
}
