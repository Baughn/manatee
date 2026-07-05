namespace Manatee.SolverBench;

/// <summary>
/// All contestants. Agents: add your backend here while testing in your
/// worktree; the central run re-adds every collected backend.
/// </summary>
public static class Registry
{
    public static Func<ISolverBackend>[] Factories =>
    [
        () => new Backends.NaiveDenseBackend(),
    ];
}
