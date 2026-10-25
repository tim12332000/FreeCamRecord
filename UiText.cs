namespace FreeCamRecord;

internal static class UiText
{
    private static bool english;
    internal static bool IsEnglish => Volatile.Read(ref english);
    internal static string Language => IsEnglish ? "en" : "zh-TW";
    internal static void SetLanguage(string? language)
    {
        bool useEnglish = language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true;
        Volatile.Write(ref english, useEnglish);
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(useEnglish ? "en" : "zh-TW");
    }
    internal static string T(string traditionalChinese, string englishText) => IsEnglish ? englishText : traditionalChinese;
}
