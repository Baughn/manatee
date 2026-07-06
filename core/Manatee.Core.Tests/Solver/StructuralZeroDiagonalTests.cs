using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// Family-B systems carry voltage-source branch rows whose diagonal is
/// structurally absent (the stamp writes only the off-diagonal ±1 incidence
/// coupling). A no-pivot LU divides by zero on the very first of these; the
/// sparse backend must pivot through them and still agree with the referee.
/// </summary>
public sealed class StructuralZeroDiagonalTests
{
    [Fact]
    public void FamilyB_has_structurally_zero_branch_diagonals()
    {
        // 100 node rows + branch rows; sources every 25 nodes ⇒ branches at 100..103.
        var system = SystemFixtures.LadderB(100, sourceEvery: 25);
        var nodeRows = system.Dimension - BranchCount(system);

        var diagonals = new HashSet<int>();
        foreach (var e in system.Pattern)
            if (e.Row == e.Column)
                diagonals.Add(e.Row);

        // Every branch row (index ≥ nodeRows) must be missing from the diagonal set.
        for (var br = nodeRows; br < system.Dimension; br++)
            Assert.DoesNotContain(br, diagonals);
        // Sanity: node rows DO have diagonals (they carry conductance).
        Assert.Contains(0, diagonals);
    }

    [Theory]
    [InlineData(100, 25)]
    [InlineData(500, 50)]
    [InlineData(1000, 50)]
    public void Sparse_solves_family_B_and_agrees_with_referee(int nodes, int every)
    {
        var system = SystemFixtures.LadderB(nodes, sourceEvery: every);

        var sparse = SolverTestHarness.SolveWith(new SparseLuBackend(), system);
        var dense = SolverTestHarness.SolveWith(new NaiveDenseBackend(), system);

        var diff = SolverTestHarness.ScaledMaxDiff(sparse, dense);
        Assert.True(diff < 1e-9, $"{system.Name}: scaled max component diff {diff:E2} ≥ 1e-9");

        var residual = system.ResidualInfNorm(sparse) / system.Scale();
        Assert.True(residual < 1e-9, $"{system.Name}: scaled residual {residual:E2} ≥ 1e-9");
    }

    // Branch rows are dimension − nodes; recover nodes from the name is fragile,
    // so recompute from the sourceEvery contract used by the generator.
    private static int BranchCount(LinearSystem system)
    {
        // For LadderB(100, 25): nodes=100, branches at 0,25,50,75 ⇒ 4.
        // dim = nodes + branches ⇒ derive branches from the known node count.
        // We only call this for the (100,25) case above.
        return system.Dimension - 100;
    }
}
