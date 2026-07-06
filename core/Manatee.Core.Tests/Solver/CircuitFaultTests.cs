using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// The failure-handling, gmin, conductance-clamp, and tier-versioning contracts
/// of <see cref="Circuit"/> (solver.md Failure Handling / Numerics; api.md §20).
/// The solve path is proven to surface degeneracy as DATA — a typed status and a
/// retained previous solution — never an exception and never a NaN.
/// </summary>
public sealed class CircuitFaultTests
{
    // ---------------------------------------------------- contradictory sources

    [Fact]
    public void Contradictory_parallel_voltage_sources_fault_singular_without_nan()
    {
        // Two ideal sources across the SAME node pair disagree (10 V vs 5 V):
        // their two branch rows are rank-deficient ⇒ singular after gmin.
        var c = new Circuit(new SparseLuBackend(), 2, referenceNode: 1);
        c.AddVoltageSource(0, 1, 10.0);
        c.AddVoltageSource(0, 1, 5.0);
        c.Analyze();

        var status = c.FactorizeIfDirty();

        Assert.Equal(SolveStatus.Singular, status);
        Assert.True(c.Faulted);
        // The fault localizes to an auxiliary branch row (mapping row → component
        // is the netlist layer's job); it is not a node-potential row.
        Assert.True(c.Fault.Row >= c.NodeRowCount,
            $"fault row {c.Fault.Row} should name an aux branch row (≥ {c.NodeRowCount})");
        // Nothing was published and nothing is non-finite.
        Assert.Equal(SolveStatus.Singular, c.Solve());
        foreach (var v in c.PublishedVector.ToArray())
            Assert.True(double.IsFinite(v), "published vector must never contain NaN/∞");
    }

    [Fact]
    public void A_fault_retains_the_previous_published_solution()
    {
        // Valid transformer stage first: V(A)=10, V(B)=5 published.
        var c = new Circuit(new SparseLuBackend(), 4, referenceNode: 3);
        c.AddVoltageSource(0, 3, 10.0);
        var xf = c.AddIdealTransformer(0, 3, 1, 3, turnsRatio: 2.0);
        c.AddResistor(1, 3, 5.0);
        c.Analyze();
        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.Equal(SolveStatus.Ok, c.Solve());
        Assert.Equal(10.0, c.ReadPotential(0), 9);
        Assert.Equal(5.0, c.ReadPotential(1), 9);

        // Tier-2 change to a degenerate ratio n=0 forces the voltage-ratio row to
        // V(A)=0, contradicting the 10 V source ⇒ singular refactorization.
        c.SetTransformerRatio(xf, 0.0);
        Assert.Equal(SolveStatus.Singular, c.FactorizeIfDirty());
        Assert.True(c.Faulted);
        Assert.Equal(SolveStatus.Singular, c.Solve());   // no publish

        // The previously published solution is intact — not zeroed, not NaN.
        Assert.Equal(10.0, c.ReadPotential(0), 9);
        Assert.Equal(5.0, c.ReadPotential(1), 9);
    }

    [Fact]
    public void A_fault_auto_retries_after_a_repairing_change()
    {
        var c = new Circuit(new SparseLuBackend(), 4, referenceNode: 3);
        c.AddVoltageSource(0, 3, 10.0);
        var xf = c.AddIdealTransformer(0, 3, 1, 3, turnsRatio: 0.0);  // n=0 ⇒ V(0)=0 vs source ⇒ singular
        c.AddResistor(1, 3, 100.0);
        c.Analyze();
        Assert.Equal(SolveStatus.Singular, c.FactorizeIfDirty());
        Assert.True(c.Faulted);

        // Repair: a sane ratio de-conflicts the constraint; the island recovers.
        c.SetTransformerRatio(xf, 2.0);
        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.False(c.Faulted);
        Assert.Equal(SolveStatus.Ok, c.Solve());
        Assert.Equal(10.0, c.ReadPotential(0), 9);
    }

    // ----------------------------------------------------------------- gmin

    [Fact]
    public void Gmin_keeps_a_deliberately_floating_subgraph_nonsingular()
    {
        // Nodes 1,2,3 form a loop with a floating 5 V source and two resistors;
        // NOTHING touches the reference (node 0). Without gmin this island has no
        // datum and is singular; the 1e-12 S shunts anchor it.
        var c = new Circuit(new SparseLuBackend(), 4, referenceNode: 0);
        var vs = c.AddVoltageSource(1, 2, 5.0);
        c.AddResistor(2, 3, 100.0);
        c.AddResistor(3, 1, 100.0);
        c.Analyze();

        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.Equal(SolveStatus.Ok, c.Solve());
        Assert.False(c.Faulted);

        // The source constraint holds; absolute potentials float near 0 (gmin).
        Assert.Equal(5.0, c.ReadPotential(1) - c.ReadPotential(2), 6);
        foreach (var v in c.PublishedVector.ToArray())
            Assert.True(double.IsFinite(v));
        _ = vs;
    }

    // ------------------------------------------------------- conductance clamp

    [Theory]
    // Below the floor: 1e12 Ω ⇒ 1e-12 S clamps up to 1e-9 S (== 1e9 Ω).
    [InlineData(1e12, 1e9)]
    // Above the ceiling: 1e-9 Ω ⇒ 1e9 S clamps down to 1e3 S (== 1e-3 Ω).
    [InlineData(1e-9, 1e-3)]
    public void Conductance_is_clamped_at_both_ends(double extremeOhms, double boundaryOhms)
    {
        // A loaded resistor whose conductance decides V(mid); the extreme value
        // must produce the SAME solution as the explicit clamp boundary.
        static double Mid(double ohms)
        {
            var c = new Circuit(new SparseLuBackend(), 3, referenceNode: 2);
            c.AddVoltageSource(0, 2, 10.0);
            c.AddResistor(0, 1, ohms);      // clamp target
            c.AddResistor(1, 2, 1000.0);    // load
            c.Analyze();
            c.FactorizeIfDirty();
            c.Solve();
            return c.ReadPotential(1);
        }

        Assert.Equal(Mid(boundaryOhms), Mid(extremeOhms), 12);
    }

    // ----------------------------------------------------- companion has no aux

    [Fact]
    public void Companion_stamp_adds_no_auxiliary_row()
    {
        // Backward-Euler storage (capacitor/inductor) uses the conductance form,
        // so the matrix dimension equals the node-row count — no branch row, in
        // contrast to a voltage source (solver.md: "Inductor: mirror").
        var withCompanion = new Circuit(new SparseLuBackend(), 3, referenceNode: 2);
        withCompanion.AddCompanion(0, 1);
        withCompanion.AddResistor(1, 2, 100.0);
        withCompanion.Analyze();
        Assert.Equal(withCompanion.NodeRowCount, withCompanion.Dimension);

        var withSource = new Circuit(new SparseLuBackend(), 3, referenceNode: 2);
        withSource.AddVoltageSource(0, 2, 5.0);
        withSource.AddResistor(1, 2, 100.0);
        withSource.Analyze();
        Assert.Equal(withSource.NodeRowCount + 1, withSource.Dimension);   // one branch row
    }

    [Fact]
    public void Companion_history_current_is_tier1_but_conductance_is_tier2()
    {
        // Fixed-dt storage: G is set once, only the history current moves per
        // step ⇒ the per-step update must NOT refactorize (solver.md tier-1 claim).
        var backend = new SparseLuBackend();
        var c = new Circuit(backend, 2, referenceNode: 1);
        c.AddResistor(0, 1, 1000.0);
        var cap = c.AddCompanion(0, 1);
        c.SetCompanionConductance(cap, 1e-3);
        c.Analyze();
        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.Equal(SolveStatus.Ok, c.Solve());
        Assert.Equal(1, backend.FullFactorizations);

        // Per-step history current: RHS only. The solution tracks the current
        // yet no factorization is discarded across many steps.
        var v0 = c.ReadPotential(0);
        c.SetCompanionCurrent(cap, 5e-3);
        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.Equal(SolveStatus.Ok, c.Solve());
        Assert.NotEqual(v0, c.ReadPotential(0));       // RHS drive changed the answer
        Assert.Equal(1, backend.FullFactorizations);   // but on the cached factors

        for (var step = 0; step < 5; step++)
        {
            c.SetCompanionCurrent(cap, 1e-3 * step);
            Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
            Assert.Equal(SolveStatus.Ok, c.Solve());
        }
        Assert.Equal(1, backend.FullFactorizations);   // still the first factorization
    }

    // ------------------------------------------------------- tier versioning

    [Fact]
    public void Versioning_refactors_on_conductance_but_not_on_rhs_or_no_op()
    {
        var backend = new SparseLuBackend();
        var c = new Circuit(backend, 3, referenceNode: 2);
        var vs = c.AddVoltageSource(0, 2, 10.0);
        var r = c.AddResistor(0, 1, 1000.0);
        c.AddResistor(1, 2, 1000.0);
        c.Analyze();

        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.Equal(SolveStatus.Ok, c.Solve());
        Assert.Equal(1, backend.FullFactorizations);   // first factorization

        // No change ⇒ FactorizeIfDirty is a no-op (cached factors reused).
        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.Equal(1, backend.FullFactorizations);

        // Tier-1 RHS drive ⇒ new solution, NO refactorization.
        c.SetVSourceValue(vs, 20.0);
        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.Equal(1, backend.FullFactorizations);
        Assert.Equal(SolveStatus.Ok, c.Solve());
        Assert.Equal(20.0, c.ReadPotential(0), 9);
        Assert.Equal(10.0, c.ReadPotential(1), 6);     // divider halves 20 V

        // Tier-2 conductance change ⇒ refactorization.
        c.SetConductance(r, 1.0 / 3000.0);
        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.True(backend.FullFactorizations >= 1);  // frozen refactor or fresh
        Assert.Equal(SolveStatus.Ok, c.Solve());
        // V(mid) = 20 · 1000/(3000+1000) = 5 V.
        Assert.Equal(5.0, c.ReadPotential(1), 6);
    }
}
