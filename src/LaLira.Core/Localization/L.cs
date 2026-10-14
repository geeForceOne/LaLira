using System.Globalization;
using System.Text.Json;

namespace LaLira.Core.Localization;

// Translations, keyed by the English text: English needs no file, and de.json maps each English
// text to its German one. A text missing from de.json shows in English; the test
// EveryTextHasAGermanTranslation lists every translated text in the code without a German entry.
public static class L
{
    public const string English = "en";
    public const string German = "de";
    public static readonly IReadOnlyList<string> Languages = [English, German];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> GermanTexts = new(Load);

    public static IReadOnlyDictionary<string, string> GermanDictionary => GermanTexts.Value;

    public static string Normalize(string? language) =>
        language is not null && language.StartsWith(German, StringComparison.OrdinalIgnoreCase) ? German : English;

    public static string Text(string language, string english) =>
        Normalize(language) == German && GermanTexts.Value.TryGetValue(english, out var german) ? german : english;

    // The month's name in that language, e.g. "Oktober".
    public static string MonthName(string language, int month) =>
        Culture(language).DateTimeFormat.GetMonthName(month);

    // Short month name without a trailing period, e.g. "Okt".
    public static string MonthShortName(string language, int month) =>
        Culture(language).DateTimeFormat.GetAbbreviatedMonthName(month).TrimEnd('.');

    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(Normalize(language));

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("LaLira.de.json")
            ?? throw new InvalidOperationException("The German texts are missing from the build.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}

// Texts for one person: their language for words, their region format for numbers and dates.
// Without arguments it follows the current request's cultures, which is what pages use;
// background jobs (emails) pass the recipient's settings.
public sealed class Texts(string? language = null, CultureInfo? format = null)
{
    public string Language => language ?? L.Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    public CultureInfo Format => format ?? CultureInfo.CurrentCulture;

    public string this[string english] => L.Text(Language, english);

    public string this[string english, params object?[] args] => string.Format(Format, L.Text(Language, english), args);

    // one/many are English texts with {0} for the count, e.g. "{0} booking" / "{0} bookings".
    public string Plural(int count, string one, string many) =>
        string.Format(Format, L.Text(Language, count == 1 ? one : many), count);
}
