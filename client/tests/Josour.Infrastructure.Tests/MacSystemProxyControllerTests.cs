using System.Globalization;
using Josour.Core.Net;
using Josour.Infrastructure.SystemProxy;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The macOS controller against a simulated <c>networksetup</c> that keeps each service's settings and prints what the real
/// one prints, so capture → apply → restore is checked end to end without touching this machine's settings.
/// </summary>
public sealed class MacSystemProxyControllerTests
{
    private const int Port = 51515;

    [Fact]
    public void Services_SkipTheNoticeAndTheDisabledOnes()
    {
        var services = MacSystemProxyController.ParseServices(
            "An asterisk (*) denotes that a network service is disabled.\nWi-Fi\n*Bluetooth PAN\nUSB 10/100/1000 LAN\nThunderbolt Bridge\n");
        Assert.Equal(new[] { "Wi-Fi", "USB 10/100/1000 LAN", "Thunderbolt Bridge" }, services);
    }

    [Fact]
    public void Proxy_IsParsedFromNetworksetupsOwnWords()
    {
        var field = MacSystemProxyController.ParseProxy("Enabled: Yes\nServer: proxy.corp\nPort: 8080\nAuthenticated Proxy Enabled: 0\n");
        Assert.True(field.Enabled);
        Assert.Equal("proxy.corp", field.Server);
        Assert.Equal(8080, field.Port);
        Assert.Empty(MacSystemProxyController.ParseBypass("There aren't any bypass domains set on Wi-Fi.\n"));
    }

    [Fact]
    public async Task CaptureApplyRestore_PutsEveryServiceBackExactly()
    {
        var ns = new FakeNetworkSetup();
        ns.Services["Wi-Fi"] = new FakeService { WebServer = "proxy.corp", WebPort = 8080, WebEnabled = true, Bypass = { "*.corp" }, Pac = true };
        ns.Services["USB 10/100/1000 LAN"] = new FakeService();
        var before = ns.Clone();
        var controller = new MacSystemProxyController(ns);
        var target = new SystemProxyTarget(Port, new[] { "josour.example.com" });

        var snapshot = await controller.CaptureAsync(CancellationToken.None);
        await controller.ApplyAsync(target, CancellationToken.None);

        Assert.True(await controller.IsAppliedAsync(target, CancellationToken.None));
        foreach (var service in ns.Services.Values)
        {
            Assert.True(service.WebEnabled && service.SecureEnabled);
            Assert.Equal("127.0.0.1", service.WebServer);
            Assert.Equal(Port, service.SecurePort);
            Assert.False(service.Pac);
            Assert.False(service.Wpad);
            Assert.Contains("josour.example.com", service.Bypass);
            Assert.Contains("192.168.0.0/16", service.Bypass);
        }

        await controller.RestoreAsync(snapshot, Port, CancellationToken.None);

        Assert.Equal(before.Describe(), ns.Describe());
        Assert.False(await controller.IsAppliedAsync(target, CancellationToken.None));
        Assert.DoesNotContain(ns.Calls, c => c.StartsWith("osascript", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_KeepsWhatTheUserChangedDuringTheSession()
    {
        var ns = new FakeNetworkSetup();
        ns.Services["Wi-Fi"] = new FakeService();
        var controller = new MacSystemProxyController(ns);
        var snapshot = await controller.CaptureAsync(CancellationToken.None);
        await controller.ApplyAsync(new SystemProxyTarget(Port, Array.Empty<string>()), CancellationToken.None);

        // The user points the secure proxy elsewhere mid-session; that is theirs to keep.
        ns.Services["Wi-Fi"].SecureServer = "mine.example";
        ns.Services["Wi-Fi"].SecurePort = 3128;

        await controller.RestoreAsync(snapshot, Port, CancellationToken.None);

        Assert.False(ns.Services["Wi-Fi"].WebEnabled);
        Assert.Equal("mine.example", ns.Services["Wi-Fi"].SecureServer);
        Assert.Equal(3128, ns.Services["Wi-Fi"].SecurePort);
        Assert.True(ns.Services["Wi-Fi"].SecureEnabled);
    }

    [Fact]
    public async Task Restore_AServiceThatIsThereButCannotBeRead_FailsTheRestore_SoTheMarkerStays()
    {
        var ns = new FakeNetworkSetup();
        ns.Services["Wi-Fi"] = new FakeService();
        ns.Services["Thunderbolt Bridge"] = new FakeService();
        var controller = new MacSystemProxyController(ns);
        var snapshot = await controller.CaptureAsync(CancellationToken.None);
        await controller.ApplyAsync(new SystemProxyTarget(Port, Array.Empty<string>()), CancellationToken.None);

        ns.WedgedReads.Add("Wi-Fi");
        var ex = await Assert.ThrowsAsync<SystemProxyException>(() => controller.RestoreAsync(snapshot, Port, CancellationToken.None));

        Assert.Equal("restore_failed", ex.Reason);
        Assert.Contains("Wi-Fi", ex.Message);
        Assert.True(ns.Services["Wi-Fi"].WebEnabled); // still ours, and still recorded for the next attempt

        // Once it answers again, the retry puts everything back.
        ns.WedgedReads.Clear();
        await controller.RestoreAsync(snapshot, Port, CancellationToken.None);
        Assert.All(ns.Services.Values, s => Assert.False(s.WebEnabled || s.SecureEnabled));
    }

    [Fact]
    public async Task Restore_AServiceThatIsGone_IsSkipped()
    {
        var ns = new FakeNetworkSetup();
        ns.Services["Wi-Fi"] = new FakeService();
        ns.Services["iPhone USB"] = new FakeService();
        var controller = new MacSystemProxyController(ns);
        var snapshot = await controller.CaptureAsync(CancellationToken.None);
        await controller.ApplyAsync(new SystemProxyTarget(Port, Array.Empty<string>()), CancellationToken.None);

        ns.Services.Remove("iPhone USB"); // unplugged
        await controller.RestoreAsync(snapshot, Port, CancellationToken.None);

        Assert.False(ns.Services["Wi-Fi"].WebEnabled);
    }

    [Fact]
    public async Task AServiceThatAppearsAfterTheCapture_IsNeverPointedAtUs_AndOneThatIsAnywayIsTurnedOff()
    {
        var ns = new FakeNetworkSetup();
        ns.Services["Wi-Fi"] = new FakeService();
        var controller = new MacSystemProxyController(ns);
        var snapshot = await controller.CaptureAsync(CancellationToken.None);

        ns.Services["USB LAN"] = new FakeService(); // enabled between the capture and the change
        await controller.ApplyAsync(new SystemProxyTarget(Port, Array.Empty<string>()), CancellationToken.None);
        Assert.False(ns.Services["USB LAN"].WebEnabled);

        // A service the snapshot does not know that points at us all the same (renamed mid-session): turned off.
        ns.Services["Wi-Fi 2"] = ns.Services["Wi-Fi"].Clone();
        await controller.RestoreAsync(snapshot, Port, CancellationToken.None);

        Assert.False(ns.Services["Wi-Fi"].WebEnabled);
        Assert.False(ns.Services["Wi-Fi 2"].WebEnabled);
        Assert.False(ns.Services["Wi-Fi 2"].SecureEnabled);
    }

    [Fact]
    public async Task Restore_DoesNotTurnPacBackOn_OverAProxyTheUserSetUpDuringTheSession()
    {
        var ns = new FakeNetworkSetup();
        ns.Services["Wi-Fi"] = new FakeService { Pac = true, Wpad = true, Bypass = { "*.corp" } };
        var controller = new MacSystemProxyController(ns);
        var snapshot = await controller.CaptureAsync(CancellationToken.None);
        await controller.ApplyAsync(new SystemProxyTarget(Port, Array.Empty<string>()), CancellationToken.None);

        var wifi = ns.Services["Wi-Fi"];
        wifi.WebServer = wifi.SecureServer = "mine.example";
        wifi.WebPort = wifi.SecurePort = 3128;

        await controller.RestoreAsync(snapshot, Port, CancellationToken.None);

        Assert.False(wifi.Pac);
        Assert.False(wifi.Wpad);
        Assert.Equal("mine.example", wifi.WebServer);
        Assert.True(wifi.WebEnabled && wifi.SecureEnabled);
    }

    [Fact]
    public void AllServices_IncludeTheDisabledOnes_WithoutTheirAsterisk()
    {
        var services = MacSystemProxyController.ParseAllServices(
            "An asterisk (*) denotes that a network service is disabled.\nWi-Fi\n*Bluetooth PAN\n");
        Assert.Equal(new[] { "Wi-Fi", "Bluetooth PAN" }, services);
    }

    [Fact]
    public async Task WhenNetworksetupNeedsAuthorisation_TheBatchRunsOnceThroughThePasswordPrompt()
    {
        var ns = new FakeNetworkSetup { RequiresAdmin = true };
        ns.Services["Wi-Fi"] = new FakeService();
        ns.Services["Dan's \"USB\" LAN"] = new FakeService();
        var controller = new MacSystemProxyController(ns);

        await controller.ApplyAsync(new SystemProxyTarget(Port, Array.Empty<string>()), CancellationToken.None);

        Assert.Single(ns.Calls, c => c.StartsWith("osascript", StringComparison.Ordinal));
        Assert.All(ns.Services.Values, s => Assert.True(s.WebEnabled && s.WebPort == Port));
    }

    [Fact]
    public async Task ACancelledPasswordPrompt_IsAuthorizationDenied()
    {
        var ns = new FakeNetworkSetup { RequiresAdmin = true, PromptCancelled = true };
        ns.Services["Wi-Fi"] = new FakeService();
        var controller = new MacSystemProxyController(ns);

        var ex = await Assert.ThrowsAsync<SystemProxyException>(
            () => controller.ApplyAsync(new SystemProxyTarget(Port, Array.Empty<string>()), CancellationToken.None));

        Assert.Equal("authorization_denied", ex.Reason);
    }

    [Fact]
    public void ScriptQuoting_SurvivesQuotesAndSpaces()
    {
        Assert.Equal("'Dan'\\''s LAN'", MacSystemProxyController.ShellQuote("Dan's LAN"));
        Assert.Equal("a \\\"b\\\" c\\\\d", MacSystemProxyController.AppleScriptEscape("a \"b\" c\\d"));
    }

    /// <summary>networksetup as a state machine over a few services, plus osascript running a batch of its commands.</summary>
    private sealed class FakeNetworkSetup : ICommandRunner
    {
        public Dictionary<string, FakeService> Services { get; private set; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = new();
        public bool RequiresAdmin { get; set; }
        public bool PromptCancelled { get; set; }

        /// <summary>Services whose <c>-get…</c> commands time out (networksetup wedged after a wake).</summary>
        public HashSet<string> WedgedReads { get; } = new(StringComparer.Ordinal);

        public FakeNetworkSetup Clone() => new() { Services = Services.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) };

        public string Describe() => string.Join("\n", Services.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ": " + kv.Value));

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            if (fileName.EndsWith("osascript", StringComparison.Ordinal))
            {
                Calls.Add("osascript " + string.Join(' ', arguments));
                if (PromptCancelled) return Task.FromResult(new CommandResult(1, "execution error: User canceled. (-128)"));
                foreach (var command in ParseBatch(arguments[1])) Execute(command, asAdmin: true);
                return Task.FromResult(new CommandResult(0, string.Empty));
            }

            Calls.Add(string.Join(' ', arguments));
            return Task.FromResult(Execute(arguments.ToArray(), asAdmin: false));
        }

        private CommandResult Execute(string[] a, bool asAdmin)
        {
            var verb = a[0];
            if (verb == "-listallnetworkservices")
            {
                return Ok("An asterisk (*) denotes that a network service is disabled.\n" + string.Join("\n", Services.Keys) + "\n*Bluetooth PAN\n");
            }

            if (!Services.TryGetValue(a[1], out var s)) return new CommandResult(4, $"** Error: The parameters were not valid.\n");
            if (verb.StartsWith("-get", StringComparison.Ordinal))
            {
                if (WedgedReads.Contains(a[1])) return new CommandResult(null, "timed out");
                return Ok(verb switch
                {
                    "-getwebproxy" => Proxy(s.WebEnabled, s.WebServer, s.WebPort),
                    "-getsecurewebproxy" => Proxy(s.SecureEnabled, s.SecureServer, s.SecurePort),
                    "-getproxybypassdomains" => s.Bypass.Count == 0 ? $"There aren't any bypass domains set on {a[1]}.\n" : string.Join("\n", s.Bypass) + "\n",
                    "-getautoproxyurl" => $"URL: http://wpad.corp/proxy.pac\nEnabled: {(s.Pac ? "Yes" : "No")}\n",
                    "-getproxyautodiscovery" => $"Auto Proxy Discovery: {(s.Wpad ? "On" : "Off")}\n",
                    _ => throw new InvalidOperationException(verb),
                });
            }

            if (RequiresAdmin && !asAdmin) return new CommandResult(14, "** Error: Command requires admin privileges.\n");
            switch (verb)
            {
                case "-setwebproxy": s.WebServer = a[2]; s.WebPort = int.Parse(a[3], CultureInfo.InvariantCulture); s.WebEnabled = true; break;
                case "-setwebproxystate": s.WebEnabled = a[2] == "on"; break;
                case "-setsecurewebproxy": s.SecureServer = a[2]; s.SecurePort = int.Parse(a[3], CultureInfo.InvariantCulture); s.SecureEnabled = true; break;
                case "-setsecurewebproxystate": s.SecureEnabled = a[2] == "on"; break;
                case "-setproxybypassdomains": s.Bypass = a.Skip(2).Where(d => d != "Empty").ToList(); break;
                case "-setautoproxystate": s.Pac = a[2] == "on"; break;
                case "-setproxyautodiscovery": s.Wpad = a[2] == "on"; break;
                default: throw new InvalidOperationException(verb);
            }
            return Ok(string.Empty);
        }

        private static string Proxy(bool enabled, string server, int port)
            => $"Enabled: {(enabled ? "Yes" : "No")}\nServer: {server}\nPort: {port}\nAuthenticated Proxy Enabled: 0\n";

        private static CommandResult Ok(string output) => new(0, output);

        /// <summary>Undoes AppleScriptEscape and ShellQuote on "do shell script \"'networksetup' 'a' 'b' &amp;&amp; …\"".</summary>
        private static IEnumerable<string[]> ParseBatch(string apple)
        {
            var start = apple.IndexOf('"') + 1;
            var end = apple.LastIndexOf("\" with administrator privileges", StringComparison.Ordinal);
            var script = apple[start..end].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            foreach (var part in script.Split(" && "))
            {
                var words = new List<string>();
                var i = 0;
                while (i < part.Length)
                {
                    if (part[i] == ' ') { i++; continue; }
                    var word = new System.Text.StringBuilder();
                    while (i < part.Length && part[i] != ' ')
                    {
                        if (part[i] == '\'')
                        {
                            var close = part.IndexOf('\'', i + 1);
                            word.Append(part, i + 1, close - i - 1);
                            i = close + 1;
                        }
                        else if (part[i] == '\\')
                        {
                            word.Append(part[i + 1]);
                            i += 2;
                        }
                        else
                        {
                            word.Append(part[i++]);
                        }
                    }
                    words.Add(word.ToString());
                }
                yield return words.Skip(1).ToArray(); // the first word is networksetup's path
            }
        }
    }

    private sealed class FakeService
    {
        public bool WebEnabled { get; set; }
        public string WebServer { get; set; } = string.Empty;
        public int WebPort { get; set; }
        public bool SecureEnabled { get; set; }
        public string SecureServer { get; set; } = string.Empty;
        public int SecurePort { get; set; }
        public List<string> Bypass { get; set; } = new();
        public bool Pac { get; set; }
        public bool Wpad { get; set; } = true;

        public FakeService Clone() => new()
        {
            WebEnabled = WebEnabled, WebServer = WebServer, WebPort = WebPort,
            SecureEnabled = SecureEnabled, SecureServer = SecureServer, SecurePort = SecurePort,
            Bypass = Bypass.ToList(), Pac = Pac, Wpad = Wpad,
        };

        /// <summary>
        /// The settings in effect. A proxy that is off is described without its server: macOS has no command to clear the
        /// field, so a restore turns ours off and leaves the address behind, where it does nothing.
        /// </summary>
        public override string ToString()
            => $"web={Proxy(WebEnabled, WebServer, WebPort)} secure={Proxy(SecureEnabled, SecureServer, SecurePort)} bypass=[{string.Join(',', Bypass)}] pac={Pac} wpad={Wpad}";

        private static string Proxy(bool enabled, string server, int port) => enabled ? $"on:{server}:{port}" : "off";
    }
}
