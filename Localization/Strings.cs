using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AtasForcedRiskManagementPlugin;

/// <summary>
/// Single-DLL multi-language strings for ATAS Forced Risk Manager.
/// The active language follows the ATAS interface language:
///   1. OFT.Localization.Strings.Culture (live ATAS culture) via reflection;
///   2. CultureInfo.CurrentUICulture;
///   3. SelectedCultureName in %APPDATA%\ATAS\Platform.cnf;
///   4. English fallback.
/// Translations live in embedded Localization/strings-*.json resources.
/// </summary>
public static class Strings
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Tables =
        new(LoadTables, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Dictionary<string, string> LanguageFallbacks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = "de-DE",
            ["es"] = "es-ES",
            ["fr"] = "fr-FR",
            ["hi"] = "hi-IN",
            ["it"] = "it-IT",
            ["ja"] = "ja-JP",
            ["ko"] = "ko-KR",
            ["pt"] = "pt-PT",
            ["ru"] = "ru-RU",
            ["zh"] = "zh-CN",
            ["zh-hans"] = "zh-CN",
            ["zh-hant"] = "zh-CN"
        };

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        const string marker = ".Localization.strings-";
        foreach (var resourceName in typeof(Strings).Assembly.GetManifestResourceNames())
        {
            var markerIndex = resourceName.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0 || !resourceName.EndsWith(".json", StringComparison.Ordinal))
                continue;

            var culture = resourceName[(markerIndex + marker.Length)..^".json".Length];
            using var stream = typeof(Strings).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                continue;
            using var reader = new StreamReader(stream);
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
            if (table != null)
                result[culture] = table;
        }

        return result;
    }

    public static string CurrentLanguage
    {
        get
        {
            var tables = Tables.Value;
            foreach (var candidate in ResolveCultureCandidates())
            {
                if (tables.ContainsKey(candidate))
                    return candidate;
                if (LanguageFallbacks.TryGetValue(candidate, out var fallback) && tables.ContainsKey(fallback))
                    return fallback;
            }

            return tables.ContainsKey("en") ? "en" : string.Empty;
        }
    }

    public static bool IsChinese => string.Equals(CurrentLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase);

    public static string Get(string key, params object[] args)
    {
        var value = Lookup(key);
        return args.Length > 0 ? string.Format(CultureInfo.InvariantCulture, value, args) : value;
    }

    private static string Lookup(string key)
    {
        var culture = CurrentLanguage;
        if (!string.IsNullOrEmpty(culture) && Tables.Value.TryGetValue(culture, out var table) && table.TryGetValue(key, out var value))
            return value;
        if (Tables.Value.TryGetValue("en", out var englishTable) && englishTable.TryGetValue(key, out var englishValue))
            return englishValue;
        return key;
    }

    private static IEnumerable<string> ResolveCultureCandidates()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                candidates.Add(name);
        }

        try
        {
            var stringsType = Type.GetType("OFT.Localization.Strings, OFT.Localization", throwOnError: false);
            var cultureProperty = stringsType?.GetProperty("Culture", BindingFlags.Public | BindingFlags.Static);
            var culture = cultureProperty?.GetValue(null) as CultureInfo;
            Add(culture?.Name);
        }
        catch
        {
            // fall through
        }

        try
        {
            Add(CultureInfo.CurrentUICulture.Name);
            Add(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        }
        catch
        {
            // fall through
        }

        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ATAS",
                "Platform.cnf");
            if (File.Exists(configPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                if (document.RootElement.TryGetProperty("SelectedCultureName", out var selected) &&
                    selected.ValueKind == JsonValueKind.String)
                {
                    Add(selected.GetString());
                }
            }
        }
        catch
        {
            // fall through
        }

        Add("en");
        return candidates;
    }

    public static string MaxLossAmount => Get(nameof(MaxLossAmount));
    public static string MaxConsecutiveLosses => Get(nameof(MaxConsecutiveLosses));
    public static string EnableWindowsShutdown => Get(nameof(EnableWindowsShutdown));
    public static string Risk => Get(nameof(Risk));
    public static string MaxLossAmountDesc => Get(nameof(MaxLossAmountDesc));
    public static string MaxConsecutiveLossesDesc => Get(nameof(MaxConsecutiveLossesDesc));
    public static string EnableWindowsShutdownDesc => Get(nameof(EnableWindowsShutdownDesc));
    public static string BlockerTitle => Get(nameof(BlockerTitle));
    public static string BlockerSubtitle => Get(nameof(BlockerSubtitle));
}
