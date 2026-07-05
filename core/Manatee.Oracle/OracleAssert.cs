namespace Manatee.Oracle;

/// <summary>
/// Tolerance comparisons per testing-strategy.md: DC values match the oracle
/// within 0.1% relative, or 1 µV/µA absolute near zero. Framework-agnostic
/// so non-xunit consumers (corpus runner, harness scripts) can use it.
/// </summary>
public static class OracleAssert
{
    public const double DefaultRelativeTolerance = 1e-3;
    public const double DefaultAbsoluteTolerance = 1e-6;

    public static void Close(
        double expected,
        double actual,
        double relativeTolerance = DefaultRelativeTolerance,
        double absoluteTolerance = DefaultAbsoluteTolerance)
    {
        if (!double.IsFinite(actual))
            throw new OracleMismatchException($"Non-finite value {actual} (expected {expected}). Finiteness is an invariant.");

        var tolerance = Math.Max(absoluteTolerance, Math.Abs(expected) * relativeTolerance);
        if (Math.Abs(expected - actual) > tolerance)
            throw new OracleMismatchException(
                $"Expected {expected:G9} but got {actual:G9} (|Δ| = {Math.Abs(expected - actual):G3} > tolerance {tolerance:G3}).");
    }
}

public sealed class OracleMismatchException(string message) : Exception(message);
