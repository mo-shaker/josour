using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Josour.Browser.Registry;
using Josour.Core.Browser;

namespace Josour.Browser;

/// <param name="Source">Where the path came from: app_paths:HKLM | app_paths:HKCU | app_paths:HKLM\WOW6432Node | default.</param>
/// <param name="PublisherVerified">null = not checked (off Windows).</param>
public sealed record BrowserLocation(BrowserKind Kind, string Path, string Source, bool? PublisherVerified, string? Publisher);

/// <summary>
/// It locates chrome.exe / msedge.exe from App Paths (HKLM, then HKCU, then WOW6432Node) and then the default paths, and verifies the Authenticode
/// signature's publisher ("Google LLC" / "Microsoft Corporation") on Windows.
/// </summary>
public sealed class BrowserLocator
{
    public const string ChromePublisher = "Google LLC";
    public const string EdgePublisher = "Microsoft Corporation";

    private readonly IRegistryReader _registry;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, (bool Verified, string? Publisher)?> _publisherCheck;
    private readonly Func<string, string?> _env;

    public BrowserLocator(IRegistryReader? registry = null, Func<string, bool>? fileExists = null, Func<string, (bool Verified, string? Publisher)?>? publisherCheck = null, Func<string, string?>? env = null)
    {
        _registry = registry ?? (OperatingSystem.IsWindows() ? WindowsRegistryReader.Instance : NullRegistryReader.Instance);
        _fileExists = fileExists ?? File.Exists;
        _publisherCheck = publisherCheck ?? (path => VerifyPublisher(path));
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    public static string ExeName(BrowserKind kind) => kind == BrowserKind.Chrome ? "chrome.exe" : "msedge.exe";
    public static string ExpectedPublisher(BrowserKind kind) => kind == BrowserKind.Chrome ? ChromePublisher : EdgePublisher;

    public BrowserLocation? Locate(BrowserKind kind)
    {
        foreach (var (path, source) in CandidatePaths(kind))
        {
            if (!_fileExists(path)) continue;
            var check = _publisherCheck(path);
            bool? verified = null;
            string? publisher = null;
            if (check is { } c)
            {
                publisher = c.Publisher;
                verified = c.Verified && string.Equals(c.Publisher, ExpectedPublisher(kind), StringComparison.OrdinalIgnoreCase);
            }
            return new BrowserLocation(kind, path, source, verified, publisher);
        }
        return null;
    }

    /// <summary>The order: App Paths in HKLM, then HKCU, then HKLM\WOW6432Node, then the default paths.</summary>
    public IReadOnlyList<(string Path, string Source)> CandidatePaths(BrowserKind kind)
    {
        var exe = ExeName(kind);
        var result = new List<(string, string)>();
        var appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe;
        var appPathsWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exe;
        Add(result, _registry.GetString(RegistryRoot.LocalMachine, appPaths, null), "app_paths:HKLM");
        Add(result, _registry.GetString(RegistryRoot.CurrentUser, appPaths, null), "app_paths:HKCU");
        Add(result, _registry.GetString(RegistryRoot.LocalMachine, appPathsWow, null), @"app_paths:HKLM\WOW6432Node");
        foreach (var p in DefaultPaths(kind, _env)) Add(result, p, "default");
        return result;
    }

    public static IReadOnlyList<string> DefaultPaths(BrowserKind kind, Func<string, string?> env)
    {
        var programFiles = env("ProgramFiles");
        var programFilesX86 = env("ProgramFiles(x86)");
        var localAppData = env("LocalAppData");
        var list = new List<string>();
        if (kind == BrowserKind.Chrome)
        {
            if (programFiles is not null) list.Add(Win(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
            if (programFilesX86 is not null) list.Add(Win(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"));
            if (localAppData is not null) list.Add(Win(localAppData, "Google", "Chrome", "Application", "chrome.exe"));
        }
        else
        {
            if (programFilesX86 is not null) list.Add(Win(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            if (programFiles is not null) list.Add(Win(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
        }
        return list;
    }

    /// <summary>Windows paths with an explicit '\' separator (Path.Combine gives '/' off Windows, and the tests run here on macOS).</summary>
    private static string Win(params string[] parts) => string.Join('\\', parts.Select(p => p.TrimEnd('\\')));

    private static void Add(List<(string, string)> list, string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0) return;
        if (list.Any(e => string.Equals(e.Item1, trimmed, StringComparison.OrdinalIgnoreCase))) return;
        list.Add((trimmed, source));
    }

    /// <summary>WINDOWS-ONLY: X509Certificate.CreateFromSignedFile, then O= from the subject. null off Windows.</summary>
    public static (bool Verified, string? Publisher)? VerifyPublisher(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return VerifyPublisherWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static (bool Verified, string? Publisher) VerifyPublisherWindows(string path)
    {
        try
        {
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            var organization = signer.GetNameInfo(X509NameType.SimpleName, false);
            var o = ExtractOrganization(signer.Subject) ?? organization;
            // We verify the publisher's identity only; the trust chain is left to SmartScreen/the system (the certificate may be expired in old files signed with a timestamp).
            return (o is not null, o);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    /// <summary>O=… from a distinguished name (handling quoted values).</summary>
    public static string? ExtractOrganization(string subject)
    {
        if (string.IsNullOrEmpty(subject)) return null;
        var parts = SplitDn(subject);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part[..eq].Trim().Equals("O", StringComparison.OrdinalIgnoreCase)) return part[(eq + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static IEnumerable<string> SplitDn(string dn)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in dn)
        {
            if (ch == '"') { quoted = !quoted; current.Append(ch); continue; }
            if (ch == ',' && !quoted) { yield return current.ToString(); current.Clear(); continue; }
            current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
