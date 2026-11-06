using Avalonia.Metadata;

// One XAML namespace for the brand controls and each app's own controls (each app maps it to its namespace too), so a
// view writes <c:Motif/> and <c:Banner/> under the same prefix.
[assembly: XmlnsDefinition("https://josour.app/controls", "Josour.Brand.Controls")]
