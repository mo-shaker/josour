using Avalonia.Controls;
using Avalonia.Layout;

namespace Josour.App.Controls;

/// <summary>
/// A row of dialog buttons in each system's order (Phase 1 decision 9): on Windows the primary button comes first and the
/// others follow; on macOS the others come first and the primary sits at the trailing edge. Mark the primary button with
/// the <c>primary</c> class (or <see cref="IsPrimaryProperty"/> for one that is not filled, such as Accept beside Reject).
/// The row aligns to the trailing edge and mirrors in a right-to-left window like any panel.
/// </summary>
public sealed class PlatformButtonRow : StackPanel
{
    public static readonly Avalonia.AttachedProperty<bool> IsPrimaryProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<PlatformButtonRow, Control, bool>("IsPrimary");

    public static bool GetIsPrimary(Control control) => control.GetValue(IsPrimaryProperty);

    public static void SetIsPrimary(Control control, bool value) => control.SetValue(IsPrimaryProperty, value);

    /// <summary>Overridable for tests; the real value is the running system.</summary>
    public static bool UseMacOrder { get; set; } = OperatingSystem.IsMacOS();

    public PlatformButtonRow()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        HorizontalAlignment = HorizontalAlignment.Right;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        var ordered = Children
            .OrderBy(c => IsPrimary(c) == UseMacOrder ? 1 : 0)
            .ToList();
        if (!ordered.SequenceEqual(Children))
        {
            Children.Clear();
            Children.AddRange(ordered);
        }
    }

    private static bool IsPrimary(Control c) => GetIsPrimary(c) || c.Classes.Contains("primary");
}
