using System.Linq;
using Manatee.Core;
using Manatee.Core.Falstad;

namespace Manatee.Core.Tests.Falstad;

/// <summary>
/// Focused unit tests for the Falstad importer's accept/reject contract
/// (falstad-format.md §7): supported elements map onto the Netlist Edit API,
/// wires union nodes, ground is the reference, unsupported/malformed lines reject
/// LOUDLY (typed, with line + code) and NEVER leave a partial document, and key
/// minting is deterministic.
/// </summary>
public sealed class FalstadImporterTests
{
    // A minimal divider: 10 V across 1k + 2k to ground, probe at the midpoint.
    private const string Divider = """
        $ 1 5.0E-6 10 50 5
        # a comment line (EA extension)
        v 0 256 0 128 0 0 0 10.0 0 0
        r 0 128 0 192 0 1000
        r 0 192 0 256 0 2000
        g 0 256 0 288 0
        O 0 192 0 160 0
        """;

    [Fact]
    public void Divider_imports_and_solves_to_the_hand_value()
    {
        var res = FalstadImporter.Import(Divider);
        Assert.True(res.HasReference);
        Assert.Empty(res.Notices);          // no presentation-only lines here
        Assert.Single(res.Probes);

        res.Netlist.SolveOperatingPoint();

        // Reference reads exactly 0; midpoint of 1k/2k across 10 V ⇒ 2000/3000·10 = 6.667 V.
        Assert.Equal(0.0, res.Netlist.Solution.Voltage(res.ReferenceNode), 9);
        Assert.True(res.TryGetProbe(0, 192, out var probe));
        Assert.Equal(1, res.CountProbesAt(0, 192));
        Assert.Equal(20.0 / 3.0, res.Netlist.Solution.Voltage(probe.Node), 6);
    }

    [Fact]
    public void HeaderTimeStep_is_recovered()
    {
        var res = FalstadImporter.Import(Divider);
        Assert.NotNull(res.HeaderTimeStep);
        Assert.Equal(5.0e-6, res.HeaderTimeStep!.Value, 12);
    }

    [Fact]
    public void Wire_unions_coincident_posts_into_one_node()
    {
        // Two wires and a resistor: (0,0)-(64,0) wire, (64,0)-(128,0) wire ⇒ all one node.
        const string txt = """
            v 0 128 0 0 0 0 0 5.0 0 0
            w 0 0 64 0 0
            w 64 0 128 0 0
            r 128 0 128 128 0 100
            g 0 128 0 160 0
            """;
        var res = FalstadImporter.Import(txt);
        Assert.True(res.TryGetNodeAt(0, 0, out var n0));
        Assert.True(res.TryGetNodeAt(64, 0, out var n64));
        Assert.True(res.TryGetNodeAt(128, 0, out var n128));
        Assert.Equal(n0, n64);
        Assert.Equal(n0, n128);
    }

    [Fact]
    public void Ground_is_the_reference_node()
    {
        var res = FalstadImporter.Import(Divider);
        res.Netlist.SolveOperatingPoint();
        Assert.True(res.TryGetNodeAt(0, 256, out var g));
        Assert.Equal(res.ReferenceNode, g);
        Assert.Equal(0.0, res.Netlist.Solution.Voltage(g), 12);
    }

    [Fact]
    public void Node_keys_are_coordinate_deterministic_across_imports()
    {
        var a = FalstadImporter.Import(Divider);
        var b = FalstadImporter.Import(Divider);
        // The node at (0,128) must carry the same coordinate-derived ExternalKey in both.
        var key = new ExternalKey(1, ((ulong)(uint)0 << 32) | (uint)128);
        Assert.True(a.Netlist.TryResolveNode(key, out _));
        Assert.True(b.Netlist.TryResolveNode(key, out _));
    }

    [Fact]
    public void Current_source_and_switch_import()
    {
        const string txt = """
            i 0 128 0 0 0 2.0
            r 0 0 0 128 0 100
            s 0 0 64 0 0 0 false
            g 0 128 0 160 0
            """;
        var res = FalstadImporter.Import(txt);   // must not throw
        Assert.True(res.HasReference);
    }

    [Fact]
    public void Sine_source_imports_but_nonzero_bias_rejects()
    {
        // wf=1 (sine), amp 5, freq 60, bias 0 ⇒ accepted.
        var ok = FalstadImporter.Import("""
            v 0 128 0 0 0 1 60 5.0 0 0
            r 0 0 0 128 0 100
            g 0 128 0 160 0
            """);
        Assert.True(ok.HasReference);

        // Nonzero bias on a sine is unrepresentable ⇒ typed rejection.
        var ex = Assert.Throws<FalstadImportException>(() => FalstadImporter.Import("""
            v 0 128 0 0 0 1 60 5.0 2.0 0
            g 0 128 0 160 0
            """));
        Assert.Contains(ex.Rejections, r => r.Code == "v");
    }

    [Theory]
    [InlineData("a 0 0 64 0 0", "a")]          // op-amp
    [InlineData("t 0 0 64 0 0 1 0.5", "t")]    // transistor
    [InlineData("172 0 0 64 0 0 0 0 5 0 0", "172")]   // adjustable rail
    [InlineData("T 0 0 64 64 0 1 1 0 0", "T")] // transformer (deferred)
    [InlineData("S 0 0 64 0 0 0 false 0 2", "S")]     // SPDT (deferred)
    [InlineData("<cir><r/></cir>", "<cir><r/></cir>")]  // XML dialect
    public void Unsupported_dump_types_reject_loudly(string line, string expectedCode)
    {
        var ex = Assert.Throws<FalstadImportException>(() =>
            FalstadImporter.Import(line + "\ng 0 0 0 16 0"));
        Assert.Contains(ex.Rejections, r => r.Code == expectedCode);
    }

    [Theory]
    [InlineData("r 0 0 0 128 0 -100")]         // non-positive resistance
    [InlineData("r 0 0 0 128 0 0")]            // zero resistance
    [InlineData("r 0 0 0 128 0 NaN")]          // NaN
    [InlineData("r 0 0 0 128 0")]              // missing resistance
    [InlineData("c 0 0 0 128 0 1e-6 3.0")]     // nonzero initial voltage (no IC support)
    [InlineData("l 0 0 0 128 0 1.0 0.5")]      // nonzero initial current
    public void Malformed_or_unrepresentable_params_reject(string line)
    {
        Assert.Throws<FalstadImportException>(() =>
            FalstadImporter.Import(line + "\nv 0 0 0 16 0 0 0 5 0 0\ng 0 128 0 160 0"));
    }

    [Fact]
    public void Rejection_lists_all_problems_and_builds_nothing()
    {
        // Two bad lines: both must appear; the exception fires before any build.
        var ex = Assert.Throws<FalstadImportException>(() => FalstadImporter.Import("""
            a 0 0 64 0 0
            r 0 0 0 128 0 -5
            g 0 0 0 16 0
            """));
        Assert.True(ex.Rejections.Count >= 2);
        Assert.Contains(ex.Rejections, r => r.Line == 1);
        Assert.Contains(ex.Rejections, r => r.Line == 2);
    }

    [Fact]
    public void Presentation_only_lines_are_noticed_not_rejected()
    {
        const string txt = """
            v 0 128 0 0 0 0 0 5.0 0 0
            r 0 0 0 128 0 100
            g 0 128 0 160 0
            o 0 64 0 4099 20 0.05 0 2 4 3
            h 1 2 3
            """;
        var res = FalstadImporter.Import(txt);   // must not throw
        Assert.Contains(res.Notices, n => n.Code == "o");
        Assert.Contains(res.Notices, n => n.Code == "h");
    }

    [Fact]
    public void Diode_default_imports()
    {
        const string txt = """
            v 0 128 0 0 0 0 0 1.0 0 0
            r 0 0 0 64 0 1000
            d 0 64 0 128 0
            g 0 128 0 160 0
            """;
        var res = FalstadImporter.Import(txt);
        res.Netlist.SolveOperatingPoint();
        Assert.True(res.HasReference);
    }

    [Fact]
    public void OnePost_rail_drives_its_connection_post_not_the_stub_end()
    {
        // A 1-post rail `R`: the electrical post is (x1,y1)=(0,0) (RailElm getPost(0)=point1,
        // getPostCount()==1); (x2,y2)=(0,64) is only the stub/symbol direction. The rail
        // drives (0,0) to 5 V; a resistor bleeds it to ground so the node is defined.
        const string txt = """
            R 0 0 0 64 0 0 40 5.0 0 0
            r 0 0 0 128 0 100
            g 0 128 0 160 0
            """;
        var res = FalstadImporter.Import(txt);
        res.Netlist.SolveOperatingPoint();
        Assert.True(res.TryGetNodeAt(0, 0, out var post));    // the real electrical post
        Assert.Equal(5.0, res.Netlist.Solution.Voltage(post), 6);
    }

    [Fact]
    public void Momentary_switch_imports_as_a_toggle_with_a_notice()
    {
        // s ... position momentary. Momentary is downgraded to a plain toggle (no
        // spring-return in the model) — but the downgrade must be user-visible.
        const string txt = """
            v 0 128 0 0 0 0 0 5.0 0 0
            r 0 0 64 0 0 100
            s 64 0 64 128 0 0 true
            g 0 128 0 160 0
            """;
        var res = FalstadImporter.Import(txt);   // must not throw
        Assert.Contains(res.Notices, n => n.Code == "s");
    }
}
