using System.Globalization;
using Avalonia.Markup.Xaml;
using Josour.Infrastructure.Localization;

namespace Josour.Mobile;

/// <summary>
/// <c>{m:Tr KeyName}</c>: one of the shared strings (<see cref="UiStringKeys"/>, the same <c>StringsAr.resx</c> and
/// <c>StringsEn.resx</c> the desktop reads), resolved once when the view loads. Switching the language (<see cref="MobileLanguage"/>)
/// therefore builds the view again.
/// </summary>
public sealed class Tr : MarkupExtension
{
    public Tr() { }

    public Tr(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => LocalizedStrings.Current[Key];

    /// <summary>The same lookup for code.</summary>
    public static string Get(string key) => LocalizedStrings.Current[key];

    public static string Format(string key, params object?[] args) =>
        string.Format(Culture, LocalizedStrings.Current[key], args);

    public static CultureInfo Culture => LocalizedStrings.Current.Culture;

    /// <summary>A technical value (an address, a device name, "12:30") kept left to right inside Arabic text.</summary>
    public static string Ltr(string? value) => LocalizedStrings.Current.Ltr(value);
}
