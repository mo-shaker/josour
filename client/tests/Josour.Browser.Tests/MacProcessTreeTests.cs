using Josour.Browser;

namespace Josour.Browser.Tests;

/// <summary>
/// The process tree is how macOS answers "did our browser start this", which is half of the control that keeps
/// the proxy serving the work browser alone. Chrome opens sockets from a child, never from the process that was
/// launched, so getting the descent wrong refuses every connection — or, worse, accepts the wrong ones.
/// </summary>
public class MacProcessTreeTests
{
    //  1
    //  └── 100  (the browser we launched)
    //      ├── 200
    //      │   └── 300
    //      └── 201
    //  └── 400  (somebody else entirely)
    private static readonly IReadOnlyDictionary<int, int> Tree = new Dictionary<int, int>
    {
        [100] = 1,
        [200] = 100,
        [300] = 200,
        [201] = 100,
        [400] = 1,
    };

    [Fact]
    public void A_direct_child_descends_from_the_browser()
    {
        Assert.True(MacProcessTree.IsDescendant(200, 100, Tree));
    }

    [Fact]
    public void A_grandchild_descends_too()
    {
        // Chrome's network service is not a direct child, and it is the process that opens the sockets.
        Assert.True(MacProcessTree.IsDescendant(300, 100, Tree));
    }

    [Fact]
    public void An_unrelated_process_does_not()
    {
        Assert.False(MacProcessTree.IsDescendant(400, 100, Tree));
    }

    [Fact]
    public void The_browser_is_not_its_own_descendant()
    {
        // OwnsProcess checks the root separately; conflating the two here would hide a bug there.
        Assert.False(MacProcessTree.IsDescendant(100, 100, Tree));
    }

    [Fact]
    public void A_process_that_is_not_in_the_tree_does_not_descend_from_anything()
    {
        Assert.False(MacProcessTree.IsDescendant(999, 100, Tree));
    }

    [Fact]
    public void A_cycle_does_not_spin_forever()
    {
        // This runs inside a connection check. A corrupt tree must cost a bounded walk, not the session.
        var cyclic = new Dictionary<int, int> { [10] = 11, [11] = 10 };

        Assert.False(MacProcessTree.IsDescendant(10, 100, cyclic));
    }

    [Fact]
    public void Descendants_finds_the_whole_subtree_and_nothing_above_it()
    {
        var found = MacProcessTree.Descendants(100, Tree).Order().ToList();

        Assert.Equal(new[] { 200, 201, 300 }, found);
    }

    [Fact]
    public void Ps_output_is_read_as_pid_and_parent()
    {
        const string output = """
              1     0
            100     1
            200   100
            """;

        var tree = MacProcessTree.Parse(output);

        Assert.Equal(1, tree[100]);
        Assert.Equal(100, tree[200]);
    }

    [Fact]
    public void Lines_that_are_not_two_numbers_are_skipped()
    {
        var tree = MacProcessTree.Parse("PID PPID\n100 1\nrubbish\n");

        Assert.Single(tree);
        Assert.Equal(1, tree[100]);
    }

    [MacFact]
    public void The_real_machine_reports_this_process_under_a_parent()
    {
        // Proves the ps invocation, which no fixture can.
        var tree = MacProcessTree.Read();

        Assert.NotEmpty(tree);
        Assert.True(tree.ContainsKey(Environment.ProcessId), "this process was missing from its own machine's tree");
    }
}
