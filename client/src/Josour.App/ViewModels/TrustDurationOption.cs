namespace Josour.App.ViewModels;

/// <summary>
/// How long a "accept from this guest automatically" rule lasts, as the request window offers it
/// (docs/ws-protocol.md section 5a).
/// <para>
/// A week first and "always" last, deliberately. Standing consent that ends on its own is the kind a host can forget to
/// review without that being dangerous; the open-ended one is available, but it is not what the box does by default.
/// </para>
/// </summary>
/// <param name="For">How long the rule lasts; <c>null</c> means until the host removes it.</param>
/// <param name="Label">What the box says.</param>
public sealed record TrustDurationOption(TimeSpan? For, string Label)
{
    public static IReadOnlyList<TrustDurationOption> Options { get; } = new[]
    {
        new TrustDurationOption(TimeSpan.FromDays(7), Strings.AutoAcceptTrustWeek),
        new TrustDurationOption(TimeSpan.FromDays(30), Strings.AutoAcceptTrustMonth),
        new TrustDurationOption(null, Strings.AutoAcceptTrustAlways),
    };

    public override string ToString() => Label;
}
