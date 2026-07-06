using System;
using CsCheck;
using Manatee.Core;

namespace Manatee.Core.Tests.Netlist;

/// <summary>
/// Property/fuzz tests (testing-strategy.md §Property and Fuzz): random linear
/// circuits driven through the PUBLIC API must, after every solve, obey the
/// conservation invariants the whole strategy rests on — KCL residual ≈ 0, every
/// value finite, and power conservation (Σ absorbed power ≈ 0). Plus a
/// conductance-range sweep spanning the solver's legal spread, which must stay
/// finite and low-residual. Fast category (no ngspice) — these run in the inner loop.
/// The math is untrusted input; these are the mechanical checks that catch a stamp
/// sign/scale bug without an oracle.
/// </summary>
public sealed class LinearInvariantProperties
{
    private static Core.Netlist NewNet() => new(new NetlistOptions
    {
        Profile = SolverProfile.Dc(0.5),
        Wiring = WiringPolicy.ExplicitOnly(),
        Debug = DebugLevel.Asserts,
    });

    // A random connected ladder: a source-driven spine to the reference with random
    // shunts, series resistors, and the occasional current source. Returns the head
    // node and the list of every added component (for the power-conservation sum).
    private static (Core.Netlist Net, NodeId Head, System.Collections.Generic.List<ComponentRef> Comps)
        BuildLadder(int seed, double rMin, double rMax, double vMax)
    {
        var rng = new Random(seed);
        var net = NewNet();
        var comps = new System.Collections.Generic.List<ComponentRef>();
        var rungs = rng.Next(2, 7);
        double LogR() => Math.Exp(rng.NextDouble() * (Math.Log(rMax) - Math.Log(rMin)) + Math.Log(rMin));

        NodeId head = default;
        ulong key = 1;
        using (var e = net.Edit())
        {
            var gnd = e.AddReferenceNode(new ExternalKey(key++));
            var prev = gnd;
            for (var i = 0; i < rungs; i++)
            {
                var n = e.AddNode(new ExternalKey(key++));
                if (i == 0)
                {
                    head = n;
                    comps.Add(e.AddVoltageSource(n, gnd, rng.NextDouble() * vMax + 1.0, new ExternalKey(key++)).AsRef());
                }
                else
                {
                    comps.Add(e.AddResistor(prev, n, LogR(), new ExternalKey(key++)).AsRef());
                }
                comps.Add(e.AddResistor(n, gnd, LogR(), new ExternalKey(key++)).AsRef());
                if (rng.NextDouble() < 0.3)
                    comps.Add(e.AddCurrentSource(n, gnd, rng.NextDouble() * 0.02 - 0.01, new ExternalKey(key++)).AsRef());
                prev = n;
            }
        }
        net.SolveOperatingPoint();
        return (net, head, comps);
    }

    [Fact]
    public void Random_ladders_satisfy_kcl_and_stay_finite()
    {
        Gen.Int[1, 100000].Sample(seed =>
        {
            var (net, head, comps) = BuildLadder(seed, rMin: 10.0, rMax: 1e5, vMax: 100.0);
            var isl = net.Islands.Of(head);

            var report = isl.CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
            Assert.True(report.AllFinite, $"seed {seed}: non-finite row {report.FirstNonFiniteRow}");
            Assert.True(report.MaxKclResidual < 1e-9,
                $"seed {seed}: KCL residual {report.MaxKclResidual:G6} A exceeds 1e-9");

            // Every readback finite.
            foreach (var c in comps)
            {
                Assert.True(double.IsFinite(net.ReadCurrent(c)), $"seed {seed}: non-finite current");
                Assert.True(double.IsFinite(net.ReadPower(c)), $"seed {seed}: non-finite power");
            }
        }, iter: 300, seed: "0000000000021", threads: 1);   // pinned: seeded RNG only (project rule)
    }

    [Fact]
    public void Random_ladders_conserve_power()
    {
        Gen.Int[1, 100000].Sample(seed =>
        {
            var (net, _, comps) = BuildLadder(seed, rMin: 100.0, rMax: 1e4, vMax: 50.0);

            // Σ power ABSORBED over every component ≈ 0 (sources deliver, resistors
            // dissipate). Scaled to the total dissipation so the bound is relative.
            double sum = 0.0, scale = 0.0;
            foreach (var c in comps)
            {
                var p = net.ReadPower(c);
                sum += p;
                scale += Math.Abs(p);
            }
            // 1e-6 relative: far below any stamp-sign/scale bug (those give order-1
            // residuals) yet above the genuine gmin-shunt leakage (1e-12 S per node,
            // uncounted in this component sum).
            Assert.True(Math.Abs(sum) <= 1e-6 * Math.Max(scale, 1e-6),
                $"seed {seed}: power residual {sum:G6} W vs throughput {scale:G6} W");
        }, iter: 300, seed: "0000000000022", threads: 1);   // pinned: seeded RNG only (project rule)
    }

    [Fact]
    public void Conductance_range_sweep_stays_finite_and_low_residual()
    {
        // Values spanning the solver's legal conductance spread (1e-3 Ω … 1e9 Ω, i.e.
        // the closed-switch to open-switch reciprocals). The direct LU must return a
        // finite, KCL-satisfying solution across the whole spread (solver.md numerics).
        Gen.Int[1, 100000].Sample(seed =>
        {
            var (net, head, comps) = BuildLadder(seed, rMin: 1e-3, rMax: 1e9, vMax: 100.0);
            var isl = net.Islands.Of(head);
            var report = isl.CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);

            Assert.True(report.AllFinite, $"seed {seed}: non-finite row {report.FirstNonFiniteRow}");
            // Extreme spreads lift the residual off machine-eps; still comfortably tiny.
            Assert.True(report.MaxKclResidual < 1e-6,
                $"seed {seed}: KCL residual {report.MaxKclResidual:G6} A exceeds 1e-6 under extreme spread");
            foreach (var c in comps)
                Assert.True(double.IsFinite(net.ReadCurrent(c)), $"seed {seed}: non-finite current");
        }, iter: 300, seed: "0000000000023", threads: 1);   // pinned: seeded RNG only (project rule)
    }
}
