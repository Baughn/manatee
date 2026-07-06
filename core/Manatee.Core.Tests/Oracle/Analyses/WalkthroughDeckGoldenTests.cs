using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Manatee.Core;
using Manatee.Core.Diagnostics;
using Manatee.Core.Falstad;
using Manatee.Core.Reduction;
using Manatee.Core.Tests.Lessons;

namespace Manatee.Core.Tests.Oracle.Analyses;

/// <summary>
/// Deterministic deck goldens for the WHOLE lesson corpus plus the §22 consumer
/// walkthrough circuits at their operating point (api.md §22.c; testing-strategy.md
/// "The math is treated as untrusted input"). Deck emission is pure text (slot order),
/// so a stamp-emission refactor surfaces here as a reviewable Verify diff BEFORE it is
/// an oracle delta. Fast category (no ngspice).
/// </summary>
public sealed class WalkthroughDeckGoldenTests
{
    private static ExternalKey K(ulong id) => new(id);

    public static IEnumerable<object[]> AllLessons()
        => LessonCorpus.Discover().Select(name => new object[] { name });

    // Every lesson deck: import the Falstad circuit, emit the op-point deck, golden it.
    [Theory]
    [MemberData(nameof(AllLessons))]
    public Task Lesson_deck(string name)
    {
        var lesson = LessonCorpus.Load(name);
        var res = FalstadImporter.Import(lesson.CircuitText);
        res.Netlist.SolveOperatingPoint();
        var island = res.Netlist.IslandOf(res.ReferenceNode);
        var deck = SpiceDeck.Emit(res.Netlist, island, new SpiceEmitOptions { Analysis = SpiceAnalysis.Op });
        Assert.Empty(deck.Unrepresentable.ToArray());   // lesson circuits are fully oracle-able (§22.c)
        return Verify(deck.Text).UseParameters(name);
    }

    // §22.a: a Stationeers-style partitioned DC network — a compacted cable run from a
    // source rail to a couple of constant-conductance loads (the adapted loads land as
    // plain resistors at op-point). Op-point deck golden.
    [Fact]
    public Task Revolt_partitioned_island_op_deck()
    {
        var net = new Core.Netlist(NetlistOptions.Stationeers(0.5));
        var g = new ConductorGraph(net, GraphOptions.PrePartitioned);
        var p = new PartitionKey(7);
        using (var b = g.BeginBulkBuild(6))
        {
            // A short bus 1-2-3-4 that series-collapses, with a tap at 2 and 4.
            b.AddSegment(new SegmentKey(1), new JunctionKey(1), new JunctionKey(2), new ConductorSpec(0.5, 1), p);
            b.AddSegment(new SegmentKey(2), new JunctionKey(2), new JunctionKey(3), new ConductorSpec(0.5, 1), p);
            b.AddSegment(new SegmentKey(3), new JunctionKey(3), new JunctionKey(4), new ConductorSpec(0.5, 1), p);
        }
        var feed = g.PortNode(new JunctionKey(1));
        var tap2 = g.PortNode(new JunctionKey(2));
        var tap4 = g.PortNode(new JunctionKey(4));
        var rail = g.ReferenceNode(p);
        using (var e = net.Edit())
        {
            e.AddVoltageSource(feed, rail, 48.0, K(0xE000_0000_0000_0000UL));
            e.AddResistor(tap2, rail, 40.0, K(0xE000_0000_0000_0001UL));   // adapted load A (linearised)
            e.AddResistor(tap4, rail, 24.0, K(0xE000_0000_0000_0002UL));   // adapted load B
        }
        net.SolveOperatingPoint();
        // Adding the source/loads rebuilt the compacted island, so re-resolve the feed
        // node by its junction before naming its island (api.md §16 re-resolution).
        var island = net.IslandOf(g.PortNode(new JunctionKey(1)));
        var deck = SpiceDeck.Emit(net, island, new SpiceEmitOptions { Analysis = SpiceAnalysis.Op });
        return Verify(deck.Text);
    }

    // §22.b: a Vintage Story two-wire island — a lamp on a Return-role node (the
    // TwoWireLeak auto-stamps the earth leak at commit) fed by a source. The AC source
    // is emitted at its op-point value. Op-point deck golden.
    [Fact]
    public Task Vs_two_wire_island_op_deck()
    {
        var net = new Core.Netlist(NetlistOptions.VintageStory(0.05));
        NodeId supply, lampNeg, earth;
        using (var e = net.Edit())
        {
            earth = e.AddReferenceNode(K(0xEAE7));
            supply = e.AddNode(K(1));
            lampNeg = e.AddNode(K(2), NodeRole.Return);      // TwoWireLeak stamps 1 MΩ to earth here
            e.AddVoltageSource(supply, earth, 24.0, K(20));
            e.AddResistor(supply, lampNeg, 60.0, K(10));     // the lamp
        }
        net.SolveOperatingPoint();
        var deck = SpiceDeck.Emit(net, net.IslandOf(supply), new SpiceEmitOptions { Analysis = SpiceAnalysis.Op });
        return Verify(deck.Text);
    }
}
