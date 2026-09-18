using Avalonia.Controls;

namespace Josour.App.Tests;

/// <summary>
/// A window shown for the duration of one test and closed after it.
/// <para>
/// Showing is what makes Avalonia apply control themes and evaluate bindings, so every test that asserts on a rendered
/// tree needs one. Closing matters as much: a window left open outlives its test and the next one then measures a
/// tree that two tests are holding.
/// </para>
/// </summary>
public readonly struct Rendered : IDisposable
{
    public Rendered(Window window)
    {
        Window = window;
        window.Show();
    }

    public Rendered(Control content, double width = 480, double height = 640)
        : this(new Window { Content = content, Width = width, Height = height })
    {
    }

    public Window Window { get; }

    public void Dispose() => Window.Close();
}
