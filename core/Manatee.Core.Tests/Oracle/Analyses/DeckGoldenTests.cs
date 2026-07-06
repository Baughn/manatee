using System.Threading.Tasks;
using Manatee.Core;
using Manatee.Core.Diagnostics;

namespace Manatee.Core.Tests.Oracle.Analyses;

/// <summary>
/// Verify goldens for two representative emitted decks — one DC operating point, one
/// Backward-Euler-matched transient — so a stamp or emission refactor shows up as a
/// reviewable deck-text diff BEFORE it is an oracle delta (api.md §22.c). Just TWO
/// decks here: the full golden wave over the lesson corpus is phase 9. Emission is
/// pure text (no ngspice), so this is a fast-category test.
/// </summary>
public sealed class DeckGoldenTests
{
    private static ExternalKey K(ulong id) => new(id);

    [Fact]
    public Task Dc_divider_deck()
    {
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });
        NodeId a, b, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); b = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, b, 1000.0, K(10));
            e.AddResistor(b, g, 2000.0, K(11));
        }
        net.SolveOperatingPoint();
        var deck = SpiceDeck.Emit(net, net.IslandOf(a), new SpiceEmitOptions { Analysis = SpiceAnalysis.Op });
        return Verify(deck.Text);
    }

    [Fact]
    public Task Transient_rc_backward_euler_deck()
    {
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Transient(0.01),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });
        NodeId a, x, g;
        using (var e = net.Edit())
        {
            a = e.AddNode(K(1)); x = e.AddNode(K(2)); g = e.AddReferenceNode(K(3));
            e.AddVoltageSource(a, g, 10.0, K(20));
            e.AddResistor(a, x, 1000.0, K(10));
            e.AddCapacitor(x, g, 1e-3, K(11), StateKey.From(K(11)));
        }
        var deck = SpiceDeck.Emit(net, net.IslandOf(a), new SpiceEmitOptions
        {
            Analysis = SpiceAnalysis.Tran(0.01, 3.0),
            MatchBackwardEuler = true,
        });
        return Verify(deck.Text);
    }
}
