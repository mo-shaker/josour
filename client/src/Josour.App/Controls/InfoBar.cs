using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Josour.App.Controls;

/// <summary>How an <see cref="InfoBar"/> reads. The values match WPF-UI's, so no call site had to change meaning.</summary>
public enum InfoBarSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// An inline message strip: an icon, an optional title and a message, coloured by <see cref="Severity"/>.
/// <para>
/// This carries most of what the app says when something is wrong — an unreachable server, a missing firewall rule, an
/// allow-list that could not be loaded — so its four severities have to stay visually distinct. The colours come from
/// the theme through <c>Controls/InfoBar.axaml</c>, and the severity is exposed as a pseudo-class so that styling it is
/// a selector rather than four converters.
/// </para>
/// </summary>
public sealed class InfoBar : TemplatedControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<InfoBar, bool>(nameof(IsOpen), defaultValue: true);

    public static readonly StyledProperty<bool> IsClosableProperty =
        AvaloniaProperty.Register<InfoBar, bool>(nameof(IsClosable), defaultValue: true);

    public static readonly StyledProperty<InfoBarSeverity> SeverityProperty =
        AvaloniaProperty.Register<InfoBar, InfoBarSeverity>(nameof(Severity));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<InfoBar, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<InfoBar, string?>(nameof(Message));

    static InfoBar()
    {
        SeverityProperty.Changed.AddClassHandler<InfoBar>((bar, _) => bar.UpdateSeverityClasses());
        IsOpenProperty.Changed.AddClassHandler<InfoBar>((bar, e) => bar.IsVisible = (bool)(e.NewValue ?? false));
        TitleProperty.Changed.AddClassHandler<InfoBar>((bar, _) => bar.UpdateHasTitle());
    }

    public InfoBar()
    {
        UpdateSeverityClasses();
        UpdateHasTitle();
    }

    /// <summary>Whether the strip is shown at all. Bound, on nearly every use, to "is there something to say".</summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>Whether the viewer may dismiss it. Most of the app's bars are not closable: they describe a state,
    /// and the state does not go away because the message did.</summary>
    public bool IsClosable
    {
        get => GetValue(IsClosableProperty);
        set => SetValue(IsClosableProperty, value);
    }

    public InfoBarSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        IsVisible = IsOpen;
        if (e.NameScope.Find("PART_CloseButton") is Button close)
        {
            close.Click += (_, _) => IsOpen = false;
        }
    }

    /// <summary>
    /// One pseudo-class per severity (<c>:informational</c>, <c>:success</c>, <c>:warning</c>, <c>:error</c>), so the
    /// theme selects the colour and the icon instead of the control deciding either.
    /// </summary>
    private void UpdateSeverityClasses()
    {
        PseudoClasses.Set(":informational", Severity == InfoBarSeverity.Informational);
        PseudoClasses.Set(":success", Severity == InfoBarSeverity.Success);
        PseudoClasses.Set(":warning", Severity == InfoBarSeverity.Warning);
        PseudoClasses.Set(":error", Severity == InfoBarSeverity.Error);
    }

    private void UpdateHasTitle() => PseudoClasses.Set(":has-title", !string.IsNullOrEmpty(Title));
}
