using System;
using Manatee.Core;
using Manatee.Oracle;

namespace Manatee.Core.Tests.Oracle.Analyses;

/// <summary>
/// A seeded handful of random linear R/V/I ladders cross-checked against ngspice
/// (testing-strategy.md property/fuzz: "a sample also cross-checked against ngspice").
/// The generator is deterministic (seeded), the circuits are connected by construction
/// (a series spine to the reference), and each is diffed node-by-node through the one
/// differ. Filter out locally: dotnet test --filter Category!=Oracle
/// </summary>
[Trait("Category", "Oracle")]
public sealed class RandomLinearOracleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(42)]
    public void Random_ladder_matches_ngspice(int seed)
    {
        var rng = new Random(seed);
        var net = new Core.Netlist(new NetlistOptions
        {
            Profile = SolverProfile.Dc(0.5),
            Wiring = WiringPolicy.ExplicitOnly(),
            Debug = DebugLevel.Asserts,
        });

        var rungs = rng.Next(3, 7);           // internal nodes along the spine
        double R() => Math.Round(rng.NextDouble() * 9900.0 + 100.0, 3);   // 100 Ω … 10 kΩ
        double V() => Math.Round(rng.NextDouble() * 40.0 + 5.0, 3);       // 5 … 45 V

        NodeId first = default, gnd = default;
        ulong key = 1;
        using (var e = net.Edit())
        {
            gnd = e.AddReferenceNode(new ExternalKey(key++));
            var prev = gnd;
            for (var i = 0; i < rungs; i++)
            {
                var n = e.AddNode(new ExternalKey(key++));
                if (i == 0)
                {
                    first = n;
                    e.AddVoltageSource(n, gnd, V(), new ExternalKey(key++));   // drive the head
                }
                else
                {
                    e.AddResistor(prev, n, R(), new ExternalKey(key++));       // series spine
                }
                e.AddResistor(n, gnd, R(), new ExternalKey(key++));            // shunt to reference
                // Occasionally inject a current source to ground for variety.
                if (rng.NextDouble() < 0.35)
                    e.AddCurrentSource(n, gnd, Math.Round(rng.NextDouble() * 0.02 - 0.01, 5), new ExternalKey(key++));
                prev = n;
            }
        }
        net.SolveOperatingPoint();
        OracleHarness.AssertDcMatches(net, net.IslandOf(first));
    }
}
