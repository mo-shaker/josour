using Josour.Browser.Registry;

namespace Josour.Browser.Tests;

internal sealed class FakeRegistry : IRegistryReader
{
    private readonly Dictionary<(RegistryRoot, string, string), object> _values = new();
    private readonly HashSet<(RegistryRoot, string)> _keys = new();

    public FakeRegistry Set(RegistryRoot root, string subKey, string? valueName, object value)
    {
        _values[(root, Norm(subKey), valueName ?? string.Empty)] = value;
        _keys.Add((root, Norm(subKey)));
        return this;
    }

    public string? GetString(RegistryRoot root, string subKey, string? valueName)
        => _values.TryGetValue((root, Norm(subKey), valueName ?? string.Empty), out var v) ? v.ToString() : null;

    public int? GetInt(RegistryRoot root, string subKey, string valueName)
        => _values.TryGetValue((root, Norm(subKey), valueName), out var v) && v is int i ? i : null;

    public bool KeyExists(RegistryRoot root, string subKey) => _keys.Contains((root, Norm(subKey)));

    private static string Norm(string s) => s.ToLowerInvariant();
}
