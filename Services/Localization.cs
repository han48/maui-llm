using System.Globalization;
using System.Resources;

namespace AIAgentLocal.Services;

/// <summary>
/// Provides localized strings based on device language or user preference.
/// </summary>
public static class L
{
    private const string LangPrefKey = "app_language";

    private static readonly ResourceManager _rm = new(
        "AIAgentLocal.Resources.Strings.AppResources",
        typeof(L).Assembly);

    private static readonly Dictionary<string, string> SupportedLanguages = new()
    {
        ["auto"] = "System Default",
        ["en"] = "English",
        ["vi"] = "Tiếng Việt",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
        ["zh"] = "中文",
        ["ru"] = "Русский",
        ["fr"] = "Français",
        ["de"] = "Deutsch",
        ["es"] = "Español",
        ["pt"] = "Português",
        ["ar"] = "العربية",
        ["hi"] = "हिन्दी",
        ["th"] = "ไทย",
        ["it"] = "Italiano",
    };

    static L()
    {
        // Called by static constructor - but also call Init() explicitly early
    }

    public static void Init()
    {
        ApplySavedLanguage();
    }

    private static void ApplySavedLanguage()
    {
        var saved = Preferences.Get(LangPrefKey, "auto");
        if (saved != "auto")
        {
            SetCulture(saved);
        }
    }

    public static void SetLanguage(string code)
    {
        Preferences.Set(LangPrefKey, code);
        if (code == "auto")
        {
            var system = CultureInfo.InstalledUICulture;
            Thread.CurrentThread.CurrentUICulture = system;
            Thread.CurrentThread.CurrentCulture = system;
            CultureInfo.CurrentUICulture = system;
            CultureInfo.CurrentCulture = system;
            CultureInfo.DefaultThreadCurrentUICulture = system;
            CultureInfo.DefaultThreadCurrentCulture = system;
        }
        else
        {
            SetCulture(code);
        }
    }

    private static void SetCulture(string code)
    {
        var culture = new CultureInfo(code);
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    public static string GetCurrentLanguageCode() => Preferences.Get(LangPrefKey, "auto");

    public static Dictionary<string, string> GetSupportedLanguages() => SupportedLanguages;

    public static string Get(string key)
    {
        try
        {
            return _rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
        catch
        {
            return key;
        }
    }

    public static string Get(string key, params object[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }

    // Convenience properties
    public static string LoadingModel => Get("LoadingModel");
    public static string FindingModel => Get("FindingModel");
    public static string NoModelSelected => Get("NoModelSelected");
    public static string TypeMessage => Get("TypeMessage");
    public static string Send => Get("Send");
    public static string SelectModel => Get("SelectModel");
    public static string Cancel => Get("Cancel");
    public static string DeviceInfo => Get("DeviceInfo");
    public static string ChatHistory => Get("ChatHistory");
    public static string DeleteConversation => Get("DeleteConversation");
    public static string DeleteWhich => Get("DeleteWhich");
    public static string Confirm => Get("Confirm");
    public static string Delete => Get("Delete");
    public static string NoConversations => Get("NoConversations");
    public static string SelectConversation => Get("SelectConversation");
    public static string DownloadIncomplete => Get("DownloadIncomplete");
    public static string CannotDownload => Get("CannotDownload");
    public static string FileDeletedRedownload => Get("FileDeletedRedownload");
    public static string ModelTooLarge => Get("ModelTooLarge");
    public static string History => Get("History");
    public static string OK => Get("OK");
    public static string NewConversation => Get("NewConversation");
    public static string SelectModelToDownload => Get("SelectModelToDownload");
}
