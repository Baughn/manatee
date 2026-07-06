using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// The production sparse LU must agree with the naive dense referee on every
/// circuit-shaped system, across sizes and across the full legal conductance
/// range. This is the standing correctness gate the backend competition ran in
/// its verifier, lifted into CI as the referee cross-check.
/// </summary>
public sealed class SolverCrossAgreementTests
{
    // Kept under the dense referee's n≤4000 ceiling. One family-A, family-B, and
    // two-wire representative at two sizes each, plus meshes.
    private static LinearSystem[] Systems() =>
    [
        SystemFixtures.LadderA(5),
        SystemFixtures.LadderA(100),
        SystemFixtures.LadderA(500),
        SystemFixtures.GridA(16),
        SystemFixtures.GridA(32),
        SystemFixtures.LadderB(100, sourceEvery: 25),
        SystemFixtures.LadderB(500),
        SystemFixtures.RandomMeshA(500, 1000),
        SystemFixtures.Ladder2W(50),
        SystemFixtures.Ladder2W(200),
        SystemFixtures.Grid2W(8),
        SystemFixtures.Grid2W(16),
    ];

    [Fact]
    public void Sparse_agrees_with_dense_referee_on_every_shape()
    {
        foreach (var system in Systems())
        {
            var sparse = SolverTestHarness.SolveWith(new SparseLuBackend(), system);
            var dense = SolverTestHarness.SolveWith(new NaiveDenseBackend(), system);

            var diff = SolverTestHarness.ScaledMaxDiff(sparse, dense);
            Assert.True(diff < 1e-9, $"{system.Name}: scaled max component diff {diff:E2} ≥ 1e-9");

            // The sparse solution also satisfies the system to machine precision.
            var residual = system.ResidualInfNorm(sparse) / system.Scale();
            Assert.True(residual < 1e-9, $"{system.Name}: scaled residual {residual:E2} ≥ 1e-9");
        }
    }

    [Theory]
    [InlineData(1e-9)]   // 1 GΩ open-switch floor
    [InlineData(1e-6)]
    [InlineData(1e-3)]
    [InlineData(1e0)]
    [InlineData(1e3)]    // 1 mΩ closed-switch / DC-inductor-short ceiling
    public void Sparse_agrees_across_the_legal_conductance_range(double siemens)
    {
        var system = SystemFixtures.LadderAtConductance(200, siemens);

        var sparse = SolverTestHarness.SolveWith(new SparseLuBackend(), system);
        var dense = SolverTestHarness.SolveWith(new NaiveDenseBackend(), system);

        var diff = SolverTestHarness.ScaledMaxDiff(sparse, dense);
        Assert.True(diff < 1e-9, $"{system.Name}: scaled max component diff {diff:E2} ≥ 1e-9");
    }
}
