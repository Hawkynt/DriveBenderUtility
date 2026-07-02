using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DivisonM.App.Localization;

/// <summary>
/// Resource-backed localization (FR-I18N): every user-facing string comes from an
/// embedded per-language JSON dictionary (English default, German shipped), selected from
/// the OS locale with a manual override. Missing keys fall back to English then to the
/// key itself, and a test asserts the sets are complete so no string is hard-coded.
/// </summary>
public sealed class Localizer {

  private const string _DEFAULT_LANGUAGE = "en";
  private static readonly string[] _SHIPPED = ["en", "de"];

  private readonly Dictionary<string, Dictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);
  private string _current = _DEFAULT_LANGUAGE;

  public static Localizer Instance { get; } = new();

  public Localizer() {
    foreach (var language in _SHIPPED)
      this._languages[language] = _LoadLanguage(language);

    this._current = _ResolveInitialLanguage();
  }

  public IReadOnlyList<string> AvailableLanguages => _SHIPPED;

  public string CurrentLanguage {
    get => this._current;
    set => this._current = this._languages.ContainsKey(value) ? value : _DEFAULT_LANGUAGE;
  }

  /// <summary>The localized string for a key; English then the raw key are the fallbacks.</summary>
  public string this[string key] => this.Get(key);

  public string Get(string key) {
    if (this._languages.TryGetValue(this._current, out var current) && current.TryGetValue(key, out var value))
      return value;
    if (this._languages.TryGetValue(_DEFAULT_LANGUAGE, out var fallback) && fallback.TryGetValue(key, out var english))
      return english;

    return key;
  }

  public string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, this.Get(key), args);

  /// <summary>All keys defined for a language — used by the completeness test.</summary>
  public IReadOnlyCollection<string> KeysOf(string language)
    => this._languages.TryGetValue(language, out var map) ? map.Keys : [];

  private static string _ResolveInitialLanguage() {
    var twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    return _SHIPPED.Contains(twoLetter, StringComparer.OrdinalIgnoreCase) ? twoLetter : _DEFAULT_LANGUAGE;
  }

  private static Dictionary<string, string> _LoadLanguage(string language) {
    var fileName = $"strings.{language}.json";
    var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
    foreach (var candidate in new[] { Path.Combine(baseDir, "Localization", fileName), Path.Combine(baseDir, fileName) })
      if (File.Exists(candidate))
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(candidate)) ?? new(StringComparer.Ordinal);

    return new(StringComparer.Ordinal);
  }

}
