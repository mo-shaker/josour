using Josour.Infrastructure.Diagnostics;

namespace Josour.Infrastructure.Tests;

/// <summary>
/// The adapter between track B's free-form <c>ITunnelSession.Diagnostics</c> and the reserved keys of docs/api.md. Two
/// things are tested here and both are contractual rather than defensive: <b>only</b> the documented keys leave, and
/// <b>only</b> IP literals travel as peers — the contract says a counter and source addresses, nothing else.
/// </summary>
public sealed class ListenerAuthDiagnosticsTests
{
    private static Dictionary<string, object?> Diagnostics(params (string Key, object? Value)[] pairs)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    [Fact]
    public void TheContractsOwnKeys_AreRead()
    {
        var read = ListenerAuthDiagnostics.TryRead(
            Diagnostics(
                ("listener_unauthenticated", 4),
                ("listener_port", 51234),
                ("unauthenticated_peers", new[] { "203.0.113.9", "2001:db8::1" })),
            out var report);

        Assert.True(read);
        Assert.Equal(4, report.UnauthenticatedCount);
        Assert.Equal(51234, report.ListenerPort);
        Assert.Equal(new[] { "203.0.113.9", "2001:db8::1" }, report.Peers);
    }

    [Fact]
    public void OnlyTheThreeDocumentedKeys_AreEverSent()
    {
        ListenerAuthDiagnostics.TryRead(
            Diagnostics(
                ("listener_unauthenticated", 2),
                ("listener_port", 4001),
                ("unauthenticated_peers", new[] { "203.0.113.9" }),
                ("winner_type", "public"),
                ("candidate_lan_ms", 12),
                ("peer_public_ip", "198.51.100.4")),
            out var report);

        var data = report.ToDiagnosticsData();

        Assert.Equal(
            new[] { "listener_port", "listener_unauthenticated", "unauthenticated_peers" },
            data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void OptionalKeys_AreOmittedRatherThanSentAsNull()
    {
        ListenerAuthDiagnostics.TryRead(Diagnostics(("listener_unauthenticated", 1)), out var report);

        var data = report.ToDiagnosticsData();

        Assert.Equal(new[] { "listener_unauthenticated" }, data.Keys.ToArray());
        Assert.Null(report.ListenerPort);
        Assert.Empty(report.Peers);
    }

    [Fact]
    public void ACountOfZeroOrLess_IsNotWorthARow()
    {
        Assert.False(ListenerAuthDiagnostics.TryRead(Diagnostics(("listener_unauthenticated", 0)), out _));
        Assert.False(ListenerAuthDiagnostics.TryRead(Diagnostics(("listener_unauthenticated", -3)), out _));
    }

    [Fact]
    public void ADictionaryWithoutTheCounters_IsNotAReport()
    {
        Assert.False(ListenerAuthDiagnostics.TryRead(null, out _));
        Assert.False(ListenerAuthDiagnostics.TryRead(new Dictionary<string, object?>(), out _));
        Assert.False(ListenerAuthDiagnostics.TryRead(Diagnostics(("winner_type", "lan")), out _));
    }

    [Fact]
    public void WhatTheTunnelActuallyPublishes_IsRead_AndItsExtraKeyIsNot()
    {
        // Exactly the shape of Josour.Tunnel.UnauthenticatedProbeLog, including the peer list as a List<string> and
        // the distinct count it adds beyond the contract — which docs/api.md does not reserve, so it stays on this machine.
        var read = ListenerAuthDiagnostics.TryRead(
            Diagnostics(
                ("listener_unauthenticated", 12),
                ("listener_port", 51234),
                ("unauthenticated_peers", new List<string> { "203.0.113.9", "198.51.100.4" }),
                ("unauthenticated_peers_distinct", 7)),
            out var report);

        Assert.True(read);
        Assert.Equal(12, report.UnauthenticatedCount);
        Assert.Equal(new[] { "203.0.113.9", "198.51.100.4" }, report.Peers);
        Assert.DoesNotContain("unauthenticated_peers_distinct", report.ToDiagnosticsData().Keys);
    }

    [Theory]
    [InlineData(7L)]
    [InlineData("7")]
    [InlineData(7.0)]
    public void ACountInAnyReasonableShape_IsUnderstood(object raw)
    {
        Assert.True(ListenerAuthDiagnostics.TryRead(Diagnostics(("listener_unauthenticated", raw)), out var report));
        Assert.Equal(7, report.UnauthenticatedCount);
    }

    [Fact]
    public void AnythingThatIsNotAnAddress_NeverLeavesThisMachine()
    {
        // The privacy rule of docs/api.md: source addresses only. A host name in that list would be a domain leak.
        ListenerAuthDiagnostics.TryRead(
            Diagnostics(
                ("listener_unauthenticated", 3),
                ("unauthenticated_peers", new object?[] { "203.0.113.9", "portal.corp", "", null, "not-an-ip" })),
            out var report);

        Assert.Equal(new[] { "203.0.113.9" }, report.Peers);
    }

    [Fact]
    public void Peers_AreDeduplicatedAndCappedAtTen()
    {
        var many = Enumerable.Range(1, 20).Select(i => $"203.0.113.{i}").Concat(new[] { "203.0.113.1" }).ToArray();

        ListenerAuthDiagnostics.TryRead(Diagnostics(("listener_unauthenticated", 21), ("unauthenticated_peers", many)), out var report);

        Assert.Equal(ListenerAuthDiagnostics.MaxPeers, report.Peers.Count);
        Assert.Equal(report.Peers.Count, report.Peers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ACommaSeparatedPeerList_IsAlsoAccepted()
    {
        ListenerAuthDiagnostics.TryRead(
            Diagnostics(("listener_unauthenticated", 2), ("unauthenticated_peers", "203.0.113.9, 198.51.100.4")),
            out var report);

        Assert.Equal(new[] { "203.0.113.9", "198.51.100.4" }, report.Peers);
    }

    [Fact]
    public void AnImpossiblePort_IsDroppedRatherThanSent()
    {
        ListenerAuthDiagnostics.TryRead(Diagnostics(("listener_unauthenticated", 1), ("listener_port", 0)), out var report);

        Assert.Null(report.ListenerPort);
    }
}
