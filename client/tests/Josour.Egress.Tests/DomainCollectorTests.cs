namespace Josour.Egress.Tests;

/// <summary>session.end.domains stays within what the server reads (MAX_REPORTED_DOMAINS).</summary>
public class DomainCollectorTests
{
    [Fact]
    public void Add_StopsRecordingAtTheCap()
    {
        var domains = new DomainCollector();
        for (var i = 0; i < DomainCollector.MaxDomains; i++)
        {
            Assert.True(domains.Add($"site{i}.example"));
        }

        Assert.False(domains.Add("one-too-many.example"));
        Assert.Equal(DomainCollector.MaxDomains, domains.Count);
        Assert.Equal(DomainCollector.MaxDomains, domains.Snapshot().Count);
        Assert.False(domains.Contains("one-too-many.example"));
    }

    [Fact]
    public void Add_RepeatedNameIsNotNew()
    {
        var domains = new DomainCollector();
        Assert.True(domains.Add("example.com"));
        Assert.False(domains.Add("example.com"));
        Assert.Equal(["example.com"], domains.Snapshot());
    }
}
