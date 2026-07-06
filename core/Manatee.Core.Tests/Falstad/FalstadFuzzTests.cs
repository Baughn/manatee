using System;
using System.Globalization;
using System.Text;
using Manatee.Core.Falstad;

namespace Manatee.Core.Tests.Falstad;

/// <summary>
/// Seeded fuzz over the accept/reject contract (task item 4). Valid Falstad text
/// generated from the importer's OWN supported subset must import without throwing;
/// invalid/unsupported text must reject with the typed
/// <see cref="FalstadImportException"/> — never a crash, never a partial document.
/// The emitter deliberately prints plain decimals (Java <c>Double.toString</c> and
/// the importer both forbid <c>+</c> in exponents, falstad-format.md §1), so its
/// output is also a round-trip check that our numbers stay parseable.
/// </summary>
public sealed class FalstadFuzzTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(9001)]
    public void Valid_generated_circuits_import_without_throwing(int seed)
    {
        var rng = new Random(seed);
        for (var iter = 0; iter < 40; iter++)
        {
            var text = EmitValid(rng);
            // Must not throw, and must produce a document with a reference.
            var res = FalstadImporter.Import(text);
            Assert.True(res.HasReference, $"seed {seed} iter {iter}:\n{text}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(9001)]
    public void Invalid_generated_circuits_reject_typed(int seed)
    {
        var rng = new Random(seed);
        for (var iter = 0; iter < 40; iter++)
        {
            var text = EmitInvalid(rng, out var why);
            // The ONLY acceptable outcome is a typed rejection — not a crash.
            var ex = Record.Exception(() => FalstadImporter.Import(text));
            Assert.True(ex is FalstadImportException,
                $"seed {seed} iter {iter} ({why}) expected FalstadImportException, got {ex?.GetType().Name ?? "no throw"}:\n{text}");
        }
    }

    // ── emitter ────────────────────────────────────────────────────────

    private static string EmitValid(Random rng)
    {
        var sb = new StringBuilder();
        sb.Append("$ 1 ").Append(Fmt(RangeDec(rng, 1e-6, 1e-3))).Append(" 10 50 5\n");
        sb.Append("# generated\n");

        // Grid of node coordinates; one ground; always at least one source to a node.
        var cols = rng.Next(2, 5);
        int G(int i) => i * 64;
        var gx = 0; var gy = (cols + 1) * 64;
        sb.Append($"g {gx} {gy} {gx} {gy + 32} 0\n");

        // A source from ground up to node column 0.
        var top = G(0);
        sb.Append($"v {gx} {gy} {top} 0 0 0 0 {Fmt(RangeDec(rng, 1, 24))} 0 0\n");

        var count = rng.Next(2, 8);
        for (var i = 0; i < count; i++)
        {
            var ax = G(rng.Next(cols)); var ay = G(rng.Next(1, cols + 1));
            var bx = G(rng.Next(cols)); var by = G(rng.Next(1, cols + 1));
            if (ax == bx && ay == by) { bx += 64; }   // avoid a zero-length self component

            switch (rng.Next(6))
            {
                case 0: sb.Append($"r {ax} {ay} {bx} {by} 0 {Fmt(RangeDec(rng, 1, 99999))}\n"); break;
                case 1: sb.Append($"w {ax} {ay} {bx} {by} 0\n"); break;
                case 2: sb.Append($"s {ax} {ay} {bx} {by} 0 {rng.Next(2)} false\n"); break;
                case 3: sb.Append($"c {ax} {ay} {bx} {by} 0 {Fmt(RangeDec(rng, 1e-9, 1e-3))} 0\n"); break;
                case 4: sb.Append($"l {ax} {ay} {bx} {by} 0 {Fmt(RangeDec(rng, 1e-3, 10))} 0\n"); break;
                case 5: sb.Append($"i {ax} {ay} {bx} {by} 0 {Fmt(RangeDec(rng, -5, 5))}\n"); break;
            }
        }

        // A probe on the source node.
        sb.Append($"O {top} 0 {top} -32 0\n");
        return sb.ToString();
    }

    private static string EmitInvalid(Random rng, out string why)
    {
        // Start from a valid circuit, then corrupt exactly one line.
        var valid = EmitValid(rng).Split('\n');
        var choice = rng.Next(6);
        string corrupt;
        switch (choice)
        {
            case 0: corrupt = "a 0 0 64 0 0"; why = "op-amp"; break;                 // unsupported code
            case 1: corrupt = "170 0 0 64 0 0"; why = "unknown-dump-type"; break;
            case 2: corrupt = "r 0 0 0 64 0 -1"; why = "negative-R"; break;
            case 3: corrupt = "r 0 0 0 64 0 NaN"; why = "nan-R"; break;
            case 4: corrupt = "v 0 0 0 64 0 3 60 5 0 0"; why = "square-wave"; break;  // wf 3 unsupported
            case 5: corrupt = "<cir><wire/></cir>"; why = "xml"; break;
            default: corrupt = "a 0 0 64 0 0"; why = "op-amp"; break;
        }
        // Insert the bad line among the good ones.
        var at = rng.Next(1, valid.Length);
        var sb = new StringBuilder();
        for (var i = 0; i < valid.Length; i++)
        {
            if (i == at) sb.Append(corrupt).Append('\n');
            sb.Append(valid[i]).Append('\n');
        }
        return sb.ToString();
    }

    private static double RangeDec(Random rng, double lo, double hi)
        => lo + rng.NextDouble() * (hi - lo);

    // Plain-decimal formatting — no exponent, no '+' (falstad-format.md §1). Rounds to
    // 10 fractional digits, which keeps small caps/inductors representable.
    private static string Fmt(double v)
    {
        var s = v.ToString("0.##########", CultureInfo.InvariantCulture);
        // Guard the invariant the whole format rests on.
        if (s.IndexOf('+') >= 0 || s.IndexOf('E') >= 0 || s.IndexOf('e') >= 0)
            throw new InvalidOperationException($"emitter produced an exponent: {s}");
        return s;
    }
}
