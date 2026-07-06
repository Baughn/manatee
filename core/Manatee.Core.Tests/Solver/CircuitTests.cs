using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// Hand-computed MNA circuits assembled through <see cref="Circuit"/>. Every
/// expected value carries the arithmetic a reviewer can redo on a calculator;
/// each linear fixture is also cross-checked against the naive dense referee, so
/// the stamp math is pinned from two directions. gmin (1e-12 S) perturbs node
/// potentials that are not constraint-pinned by ~1e-8 relative — hence the
/// 1e-6…1e-9 tolerances rather than machine precision on those.
/// </summary>
public sealed class CircuitTests
{
    private static Circuit Solve(ISolverBackend backend, int nodes, int reference, Action<Circuit> build)
    {
        var c = new Circuit(backend, nodes, reference);
        build(c);
        c.Analyze();
        Assert.Equal(SolveStatus.Ok, c.FactorizeIfDirty());
        Assert.Equal(SolveStatus.Ok, c.Solve());
        return c;
    }

    // ---------------------------------------------------------------- divider

    [Fact]
    public void Voltage_divider_splits_by_resistor_ratio()
    {
        // A=0 (+10 V source), B=1 (mid), ground=2. R1=1k A–B, R2=2k B–gnd.
        // V(B) = 10 · R2/(R1+R2) = 10 · 2000/3000 = 6.6667 V.
        // Source branch current = −(V(A)−V(B))/R1 = −(10−6.6667)/1000 = −3.3333 mA.
        VSourceStamp vs = default;
        var c = Solve(new SparseLuBackend(), 3, reference: 2, b =>
        {
            vs = b.AddVoltageSource(0, 2, 10.0);
            b.AddResistor(0, 1, 1000.0);
            b.AddResistor(1, 2, 2000.0);
        });

        Assert.Equal(10.0, c.ReadPotential(0), 9);
        Assert.Equal(20.0 / 3.0, c.ReadPotential(1), 6);
        Assert.Equal(0.0, c.ReadPotential(2), 12);         // reference reads exactly 0
        Assert.Equal(-1.0 / 300.0, c.ReadFlow(vs.AuxRow), 9);   // −3.3333 mA
    }

    // -------------------------------------------------------------- wheatstone

    [Fact]
    public void Wheatstone_bridge_unbalanced_carries_bridge_current()
    {
        // A=0 (+10 V), B=1, C=2, ground=3. R1(A-B)=100, R2(A-C)=100,
        // R3(B-gnd)=100, R4(C-gnd)=300, R5 bridge(B-C)=200.
        // Node B: 0.025·V_B − 0.005·V_C = 0.1 ; Node C: −0.005·V_B + 0.0183̄·V_C = 0.1
        // ⇒ V_B = 70/13 = 5.384615, V_C = 90/13 = 6.923077.
        // Bridge current I5 = (V_B − V_C)/R5 = (−20/13)/200 = −1/130 = −7.6923 mA.
        ConductanceStamp bridge = default;
        var c = Solve(new SparseLuBackend(), 4, reference: 3, b =>
        {
            b.AddVoltageSource(0, 3, 10.0);
            b.AddResistor(0, 1, 100.0);
            b.AddResistor(0, 2, 100.0);
            b.AddResistor(1, 3, 100.0);
            b.AddResistor(2, 3, 300.0);
            bridge = b.AddResistor(1, 2, 200.0);
        });

        Assert.Equal(70.0 / 13.0, c.ReadPotential(1), 6);
        Assert.Equal(90.0 / 13.0, c.ReadPotential(2), 6);
        var i5 = (c.ReadPotential(1) - c.ReadPotential(2)) / 200.0;
        Assert.Equal(-1.0 / 130.0, i5, 9);
        _ = bridge;
    }

    // ------------------------------------------------------- series-aiding V's

    [Fact]
    public void Two_voltage_sources_in_series_aid()
    {
        // ground=0, mid M=1, top A=2. VS1: M(+)–gnd = 6 V. VS2: A(+)–M = 6 V.
        // ⇒ V(M)=6, V(A)=12. Load R=1k A–gnd ⇒ I = 12/1000 = 12 mA through both
        // series sources (branch currents equal, −12 mA by the leaving-+ sign).
        VSourceStamp vs1 = default, vs2 = default;
        var c = Solve(new SparseLuBackend(), 3, reference: 0, b =>
        {
            vs1 = b.AddVoltageSource(1, 0, 6.0);
            vs2 = b.AddVoltageSource(2, 1, 6.0);
            b.AddResistor(2, 0, 1000.0);
        });

        Assert.Equal(6.0, c.ReadPotential(1), 9);
        Assert.Equal(12.0, c.ReadPotential(2), 9);
        Assert.Equal(-0.012, c.ReadFlow(vs1.AuxRow), 8);
        Assert.Equal(-0.012, c.ReadFlow(vs2.AuxRow), 8);
    }

    // --------------------------------------------------- current into parallel

    [Fact]
    public void Current_source_into_parallel_resistors()
    {
        // I=2 A into node A=0 (from ground=1). Two 100 Ω from A to ground.
        // R_parallel = 50 Ω ⇒ V(A) = 2 · 50 = 100 V.
        var c = Solve(new SparseLuBackend(), 2, reference: 1, b =>
        {
            b.AddCurrentSource(1, 0, 2.0);   // from ground to A: injects +2 A at A
            b.AddResistor(0, 1, 100.0);
            b.AddResistor(0, 1, 100.0);
        });

        Assert.Equal(100.0, c.ReadPotential(0), 6);
    }

    // ------------------------------------------------------------- transformer

    [Fact]
    public void Ideal_transformer_2to1_conserves_power_to_machine_precision()
    {
        // Primary a = (A=0, gnd=3), secondary b = (B=1, gnd=3), n=2 ⇒ V_a = 2·V_b.
        // Source 10 V on A; load 5 Ω on B. ⇒ V(B)=5, I_load=1 A, P_load=5 W.
        // i_s = −1 A, i_p = 0.5 A, P_primary = 10·0.5 = 5 W.
        // The two constraint rows make V_a·i_p + V_b·i_s ≡ 0 to solve residual.
        TransformerStamp xf = default;
        var c = Solve(new SparseLuBackend(), 4, reference: 3, b =>
        {
            b.AddVoltageSource(0, 3, 10.0);
            xf = b.AddIdealTransformer(0, 3, 1, 3, turnsRatio: 2.0);
            b.AddResistor(1, 3, 5.0);
        });

        var vA = c.ReadPotential(0);
        var vB = c.ReadPotential(1);
        var iP = c.ReadFlow(xf.PrimaryAuxRow);
        var iS = c.ReadFlow(xf.SecondaryAuxRow);

        Assert.Equal(10.0, vA, 9);
        Assert.Equal(5.0, vB, 9);
        Assert.Equal(0.5, iP, 8);
        Assert.Equal(-1.0, iS, 8);

        // Turns-ratio and ampere-turn constraints hold to machine precision.
        Assert.Equal(0.0, vA - 2.0 * vB, 12);
        Assert.Equal(0.0, iS + 2.0 * iP, 12);
        // Port power balance P_in + P_out == 0 (the "P_in == P_out" claim).
        Assert.Equal(0.0, vA * iP + vB * iS, 10);
    }

    // --------------------------------------------------------------- companion

    // The cross-agreement / KCL fixtures cannot pin the companion stamp: both
    // backends run the identical SetCompanion* code (so they always agree) and
    // A·x=b holds for whatever RHS is stamped (so the KCL residual is invariant
    // under a sign or scale error). These fixtures instead pin an OBSERVED node
    // potential to a hand-computed absolute, so a flipped sign or wrong scale in
    // SetCompanionConductance / SetCompanionCurrent fails a test.

    [Fact]
    public void Companion_conductance_and_current_set_an_unpinned_node()
    {
        // A=0 free, ground=1. Companion(0,1): parallel g with history source Ieq
        // injected at A. Load gL = 1/1000 S from A to ground. Nothing pins A, so
        // its KCL is (g+gL)·V(A) = Ieq ⇒ V(A) = Ieq/(g+gL) = 2e-3/2e-3 = 1.0 V.
        // Pins SetCompanionConductance magnitude/sign AND the RhsA sign of
        // SetCompanionCurrent: any of those wrong moves V(A) off +1.
        var c = Solve(new SparseLuBackend(), 2, reference: 1, b =>
        {
            var cap = b.AddCompanion(0, 1);
            b.SetCompanion(cap, siemens: 1e-3, iEq: 2e-3);
            b.AddResistor(0, 1, 1000.0);
        });

        Assert.Equal(1.0, c.ReadPotential(0), 8);
    }

    [Fact]
    public void Companion_history_current_reverses_with_terminal_order()
    {
        // Mirror of the above but the companion is added ground→A: now the
        // history source extracts at A via the RhsB path, so (g+gL)·V(A) = −Ieq
        // ⇒ V(A) = −1.0 V. Pins the RhsB sign of SetCompanionCurrent.
        var c = Solve(new SparseLuBackend(), 2, reference: 1, b =>
        {
            var cap = b.AddCompanion(1, 0);
            b.SetCompanion(cap, siemens: 1e-3, iEq: 2e-3);
            b.AddResistor(0, 1, 1000.0);
        });

        Assert.Equal(-1.0, c.ReadPotential(0), 8);
    }

    // ---------------------------------------------------------- faulted islands

    // A backend that reports every matrix as singular, counting Factorize calls.
    // Lets us assert the Circuit-level retry policy directly (solver.md Failure
    // Handling) without depending on a real backend's internal fault state.
    private sealed class CountingSingularBackend : ISolverBackend
    {
        public int FactorizeCalls;
        public string Name => "counting-singular";
        public int LastSingularColumn => 0;
        public void Analyze(int dimension, ReadOnlyMemory<MatrixEntry> pattern) { }
        public void Factorize(ReadOnlySpan<double> values)
        {
            FactorizeCalls++;
            throw new InvalidOperationException("singular");
        }
        public void Solve(ReadOnlySpan<double> rhs, Span<double> solution) { }
    }

    [Fact]
    public void Unchanged_faulted_island_does_not_refactorize_every_call()
    {
        // solver.md: a faulted island holds its previous solution and retries
        // ONLY on the next tier-2/3 change. Repeated FactorizeIfDirty with no
        // change must not re-run the (allocating) factorization.
        var backend = new CountingSingularBackend();
        var c = new Circuit(backend, 2, referenceNode: 1);
        c.AddResistor(0, 1, 1000.0);
        c.Analyze();

        Assert.Equal(SolveStatus.Singular, c.FactorizeIfDirty());
        Assert.Equal(1, backend.FactorizeCalls);

        for (var i = 0; i < 5; i++)
            Assert.Equal(SolveStatus.Singular, c.FactorizeIfDirty());
        Assert.Equal(1, backend.FactorizeCalls);
    }

    [Fact]
    public void Faulted_island_retries_after_a_repairing_change()
    {
        // A tier-2 change bumps the values epoch, moving the factorization
        // signature, so the faulted island retries (here still singular, but the
        // point is the backend IS called again — the wedge is released).
        var backend = new CountingSingularBackend();
        var c = new Circuit(backend, 2, referenceNode: 1);
        var r = c.AddResistor(0, 1, 1000.0);
        c.Analyze();

        Assert.Equal(SolveStatus.Singular, c.FactorizeIfDirty());
        Assert.Equal(1, backend.FactorizeCalls);

        c.SetConductance(r, 1.0 / 500.0);
        Assert.Equal(SolveStatus.Singular, c.FactorizeIfDirty());
        Assert.Equal(2, backend.FactorizeCalls);
    }

    // ------------------------------------------------------- duplicate summing

    [Fact]
    public void Parallel_resistors_sum_like_one_combined_resistor()
    {
        // Two 100 Ω in parallel between the same nodes stamp into the SAME
        // pattern cells (duplicate summing) and must equal a single 50 Ω.
        var two = Solve(new SparseLuBackend(), 2, reference: 1, b =>
        {
            b.AddVoltageSource(0, 1, 10.0);
            b.AddResistor(0, 1, 100.0);
            b.AddResistor(0, 1, 100.0);
        });
        var one = Solve(new SparseLuBackend(), 2, reference: 1, b =>
        {
            b.AddVoltageSource(0, 1, 10.0);
            b.AddResistor(0, 1, 50.0);
        });

        // Same node potential and same source branch current (I = 10/50 = 0.2 A).
        Assert.Equal(one.ReadPotential(0), two.ReadPotential(0), 12);
        Assert.Equal(one.ReadFlow(1), two.ReadFlow(1), 12);
        Assert.Equal(-0.2, two.ReadFlow(1), 9);
    }

    // ------------------------------------------------ cross-agreement + KCL

    // Each fixture builds, analyzes, factorizes and solves a Circuit on the
    // given backend; the topology/values are identical across backends, so
    // handle indices line up and PublishedVector is directly comparable.
    private static readonly (string Name, Func<ISolverBackend, Circuit> Build)[] Fixtures =
    [
        ("divider", b => Build(b, 3, 2, c =>
        {
            c.AddVoltageSource(0, 2, 10.0);
            c.AddResistor(0, 1, 1000.0);
            c.AddResistor(1, 2, 2000.0);
        })),
        ("wheatstone", b => Build(b, 4, 3, c =>
        {
            c.AddVoltageSource(0, 3, 10.0);
            c.AddResistor(0, 1, 100.0);
            c.AddResistor(0, 2, 100.0);
            c.AddResistor(1, 3, 100.0);
            c.AddResistor(2, 3, 300.0);
            c.AddResistor(1, 2, 200.0);
        })),
        ("series-vsources", b => Build(b, 3, 0, c =>
        {
            c.AddVoltageSource(1, 0, 6.0);
            c.AddVoltageSource(2, 1, 6.0);
            c.AddResistor(2, 0, 1000.0);
        })),
        ("isource-parallel", b => Build(b, 2, 1, c =>
        {
            c.AddCurrentSource(1, 0, 2.0);
            c.AddResistor(0, 1, 100.0);
            c.AddResistor(0, 1, 100.0);
        })),
        ("transformer-2to1", b => Build(b, 4, 3, c =>
        {
            c.AddVoltageSource(0, 3, 10.0);
            c.AddIdealTransformer(0, 3, 1, 3, 2.0);
            c.AddResistor(1, 3, 5.0);
        })),
        ("companion-rc", b => Build(b, 2, 1, c =>
        {
            // A capacitor companion driven to a fixed BE step: G = C/dt, Ieq set.
            c.AddVoltageSource(0, 1, 5.0);
            c.AddResistor(0, 1, 200.0);
            var cap = c.AddCompanion(0, 1);
            c.SetCompanion(cap, siemens: 1e-4, iEq: 3e-4);
        })),
    ];

    private static Circuit Build(ISolverBackend backend, int nodes, int reference, Action<Circuit> build)
    {
        var c = new Circuit(backend, nodes, reference);
        build(c);
        c.Analyze();
        c.FactorizeIfDirty();
        c.Solve();
        return c;
    }

    [Fact]
    public void Every_fixture_agrees_with_the_dense_referee()
    {
        foreach (var (name, build) in Fixtures)
        {
            var sparse = build(new SparseLuBackend());
            var dense = build(new NaiveDenseBackend());
            var diff = SolverTestHarness.ScaledMaxDiff(sparse.PublishedVector, dense.PublishedVector);
            Assert.True(diff < 1e-9, $"{name}: sparse vs dense scaled diff {diff:E2} ≥ 1e-9");
        }
    }

    [Fact]
    public void Every_fixture_satisfies_KCL_at_every_node()
    {
        foreach (var (name, build) in Fixtures)
        {
            var c = build(new SparseLuBackend());
            var residual = c.MaxNodeKclResidual();
            Assert.True(residual < 1e-9, $"{name}: worst node KCL residual {residual:E2} ≥ 1e-9");
        }
    }
}
