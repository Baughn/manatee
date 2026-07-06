using System;
using Manatee.Core;
using Manatee.Core.Diagnostics;

namespace Manatee.Oracle;

/// <summary>
/// The systematic ngspice differ (api.md §22.c; testing-strategy.md): take a live
/// <see cref="Netlist"/> + island + analysis, emit the deck, run ngspice, and diff
/// every mapped node voltage — and every V-source branch current the deck ammeters —
/// against the manatee <see cref="Solution"/>. DC diffs at 0.1 % rel / 1 µV|µA abs
/// (testing-strategy.md); transient diffs at matched timesteps with a caller-chosen
/// (documented, looser) tolerance for the Backward-Euler-vs-ngspice gap.
///
/// <para>This is the one path every component × analysis pin flows through, so a new
/// stamp is oracle-covered by adding a call here, not a bespoke deck.</para>
/// </summary>
public static class OracleHarness
{
    /// <summary>
    /// Solve the operating point, emit an <c>.op</c> deck, run ngspice, and diff all
    /// node voltages and V-source branch currents. Returns the deck (for Verify goldens).
    /// </summary>
    public static DeckResult AssertDcMatches(
        Netlist net, IslandId island,
        double relTol = OracleAssert.DefaultRelativeTolerance,
        double absTol = OracleAssert.DefaultAbsoluteTolerance)
    {
        var deck = SpiceDeck.Emit(net, island, new SpiceEmitOptions { Analysis = SpiceAnalysis.Op });
        var raw = new NgspiceRunner().RunDeck(deck.Text);

        foreach (var (node, col) in deck.NodeNames)
            OracleAssert.Close(raw.Get(col), net.Solution.Voltage(node), relTol, absTol);
        foreach (var (comp, col) in deck.BranchNames)
            OracleAssert.Close(raw.Get(col), net.ReadCurrent(comp), relTol, absTol);

        return deck;
    }

    /// <summary>
    /// Emit a Backward-Euler-matched <c>.tran</c> deck from the netlist's CURRENT state
    /// (call on a fresh netlist ⇒ IC = 0), then <paramref name="stepManatee"/> advances
    /// the same netlist to <paramref name="stop"/>, then ngspice runs the deck and the
    /// final timestep is diffed against the manatee solution node-by-node. The deck is
    /// captured BEFORE stepping so ngspice reproduces the from-cold trajectory manatee
    /// just walked (both Backward Euler at the same dt).
    /// </summary>
    public static DeckResult AssertTranMatches(
        Netlist net, IslandId island, double step, double stop, Action stepManatee,
        double relTol, double absTol = OracleAssert.DefaultAbsoluteTolerance)
    {
        var deck = SpiceDeck.Emit(net, island, new SpiceEmitOptions
        {
            Analysis = SpiceAnalysis.Tran(step, stop),
            MatchBackwardEuler = true,
        });

        stepManatee();

        var raw = new NgspiceRunner().RunDeck(deck.Text);
        var last = raw.PointCount - 1;
        foreach (var (node, col) in deck.NodeNames)
            OracleAssert.Close(raw.Get(col, last), net.Solution.Voltage(node), relTol, absTol);

        return deck;
    }
}
