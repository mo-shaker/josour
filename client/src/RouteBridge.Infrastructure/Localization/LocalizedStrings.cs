using System.Collections;
using System.Globalization;
using System.Resources;

namespace RouteBridge.Infrastructure.Localization;

/// <summary>
/// One language's UI text, read once from the resource set embedded in this assembly
/// (<c>StringsAr.resx</c> / <c>StringsEn.resx</c>) and looked up by the names in <see cref="UiStringKeys"/>.
/// <para>
/// <b>Why both files live in the main assembly.</b> Neither file carries a culture suffix, so the SDK embeds both here
/// instead of producing satellite assemblies — the repository pins <c>SatelliteResourceLanguages</c> to <c>en</c>, which
/// would silently drop an <c>*.ar.resx</c> satellite from the output and leave Arabic users looking at English.
/// </para>
/// <para>
/// The language is picked once at start-up (<see cref="UseLanguage"/>) before any window exists and never changes while the
/// app runs, which is why <see cref="Current"/> is a plain static: WPF evaluates <c>{x:Static app:Strings.X}</c> at load
/// time and would not follow a live change anyway.
/// </para>
/// </summary>
public sealed class LocalizedStrings
{
    private const string ResourceNamespace = "RouteBridge.Infrastructure.Localization.";

    /// <summary>U+202A: everything after it is laid out left to right until <see cref="PopDirectionalFormatting"/>.</summary>
    public const char LeftToRightEmbedding = '\u202A';

    /// <summary>U+202C: ends the embedding opened by <see cref="LeftToRightEmbedding"/>.</summary>
    public const char PopDirectionalFormatting = '\u202C';

    private static readonly Lazy<LocalizedStrings> ArabicPack = new(() => Load(UiLanguage.Arabic), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<LocalizedStrings> EnglishPack = new(() => Load(UiLanguage.English), LazyThreadSafetyMode.ExecutionAndPublication);
    private static LocalizedStrings? _current;

    private readonly IReadOnlyDictionary<string, string> _values;

    private LocalizedStrings(UiLanguage language, IReadOnlyDictionary<string, string> values)
    {
        Language = language;
        Culture = language.CreateCulture();
        _values = values;
    }

    /// <summary>The language the app is running in; Arabic until <see cref="UseLanguage"/> says otherwise.</summary>
    public static LocalizedStrings Current => _current ?? For(UiLanguages.Default);

    /// <summary>Fixes the language for the rest of the process. Call once, at start-up, before the first window.</summary>
    public static LocalizedStrings UseLanguage(UiLanguage language)
    {
        var pack = For(language);
        Interlocked.Exchange(ref _current, pack);
        return pack;
    }

    /// <summary>The (cached) pack of one language, whatever the app is currently running in.</summary>
    public static LocalizedStrings For(UiLanguage language) =>
        language == UiLanguage.English ? EnglishPack.Value : ArabicPack.Value;

    public UiLanguage Language { get; }

    /// <summary>The culture the UI formats with (Western digits in both languages — see <see cref="UiLanguages.CreateCulture"/>).</summary>
    public CultureInfo Culture { get; }

    public bool IsRightToLeft => Language.IsRightToLeft();

    /// <summary>Every key/value of this language (the tests compare the two packs and <see cref="UiStringKeys"/>).</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The text for a <see cref="UiStringKeys"/> name.</summary>
    public string this[string key] => Get(key);

    /// <summary>
    /// The text for a <see cref="UiStringKeys"/> name. A key that is missing from the resources returns the key itself:
    /// a wrong-looking label is a far better failure in front of a user than an exception in a XAML binding, and
    /// <c>LocalizationTests</c> makes sure it cannot happen for a shipped key.
    /// </summary>
    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _values.TryGetValue(key, out var value) ? value : key;
    }

    public bool TryGet(string key, out string value) => _values.TryGetValue(key, out value!);

    /// <summary>Formats a <c>…Format</c> string with <see cref="Culture"/> (so numbers keep Western digits).</summary>
    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments ?? Array.Empty<object?>());

    /// <summary>
    /// Wraps a technical value — an IP address, a port, a host name, a version, a duration, an error code — so the bidi
    /// algorithm lays it out left to right inside right-to-left prose. Without it "SARA-LAPTOP · 192.168.1.20" reorders
    /// its parts on screen. A no-op in a left-to-right language, and on null/empty input.
    /// </summary>
    public string Ltr(string? value) => Ltr(value, IsRightToLeft);

    /// <summary>See <see cref="Ltr(string?)"/>; <paramref name="rightToLeft"/> makes it testable without a current language.</summary>
    public static string Ltr(string? value, bool rightToLeft)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return rightToLeft ? LeftToRightEmbedding + value + PopDirectionalFormatting : value;
    }

    private static LocalizedStrings Load(UiLanguage language)
    {
        var baseName = ResourceNamespace + (language == UiLanguage.English ? "StringsEn" : "StringsAr");
        var manager = new ResourceManager(baseName, typeof(LocalizedStrings).Assembly);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        // The resources have no culture suffix, so the neutral set is the only one and holds everything.
        var set = manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)
            ?? throw new MissingManifestResourceException($"The embedded resource '{baseName}.resources' is missing.");

        foreach (DictionaryEntry entry in set)
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                values[key] = value;
            }
        }

        return new LocalizedStrings(language, values);
    }
}
