using System.Globalization;
using System.Text;

namespace Manatee.SolverBench;

/// <summary>
/// A concrete MNA system A·x = b plus (for family-A/B circuit cases) the
/// equivalent SPICE netlist, so ngspice can vouch for the generator itself.
/// </summary>
public sealed class LinearSystem
{
    public required string Name { get; init; }
    public required int Dimension { get; init; }
    public required MatrixEntry[] Pattern { get; init; }
    public required double[] Values { get; init; }
    public required double[] Rhs { get; init; }
    /// <summary>Null when no oracle netlist exists (synthetic systems).</summary>
    public string? SpiceNetlist { get; init; }
    /// <summary>Rawfile variable per unknown ("v(n3)", "i(v1)"), aligned with x.</summary>
    public string[]? SpiceVariables { get; init; }

    public override string ToString() => Name;

    /// <summary>y = A·x, for residual checks.</summary>
    public double[] Multiply(ReadOnlySpan<double> x)
    {
        var y = new double[Dimension];
        for (var k = 0; k < Pattern.Length; k++)
            y[Pattern[k].Row] += Values[k] * x[Pattern[k].Column];
        return y;
    }

    public double ResidualInfNorm(ReadOnlySpan<double> x)
    {
        var ax = Multiply(x);
        double worst = 0;
        for (var i = 0; i < Dimension; i++)
            worst = Math.Max(worst, Math.Abs(ax[i] - Rhs[i]));
        return worst;
    }
}

/// <summary>
/// Deterministic MNA system generators. Family A: resistors + current
/// sources (SPD). Family B: adds voltage-source branch rows (unsymmetric,
/// structurally zero diagonals — the case that punishes naive pivoting).
/// </summary>
public static class Circuits
{
    private sealed class Builder(int dimension)
    {
        private readonly Dictionary<(int Row, int Col), double> _entries = new();
        public int Dimension { get; } = dimension;

        public void Add(int row, int col, double value)
        {
            _entries.TryGetValue((row, col), out var existing);
            _entries[(row, col)] = existing + value;
        }

        /// <summary>Conductance stamp for a resistor between node indices (−1 = ground).</summary>
        public void StampResistor(int a, int b, double ohms)
        {
            var g = 1.0 / ohms;
            if (a >= 0) Add(a, a, g);
            if (b >= 0) Add(b, b, g);
            if (a >= 0 && b >= 0) { Add(a, b, -g); Add(b, a, -g); }
        }

        /// <summary>Voltage source E from node p (+) to ground, branch unknown at index br.</summary>
        public void StampGroundedVoltageSource(int p, int br, double volts, double[] rhs)
        {
            Add(p, br, 1.0);
            Add(br, p, 1.0);
            rhs[br] = volts;
        }

        /// <summary>Floating voltage source E from p (+) to q (−), branch unknown at br.</summary>
        public void StampVoltageSource(int p, int q, int br, double volts, double[] rhs)
        {
            Add(p, br, 1.0);
            Add(br, p, 1.0);
            Add(q, br, -1.0);
            Add(br, q, -1.0);
            rhs[br] = volts;
        }

        public (MatrixEntry[] Pattern, double[] Values) Build()
        {
            var keys = _entries.Keys.OrderBy(k => k.Row).ThenBy(k => k.Col).ToArray();
            var pattern = new MatrixEntry[keys.Length];
            var values = new double[keys.Length];
            for (var k = 0; k < keys.Length; k++)
            {
                pattern[k] = new MatrixEntry(keys[k].Row, keys[k].Col);
                values[k] = _entries[keys[k]];
            }
            return (pattern, values);
        }
    }

    private static string Ohms(double v) => v.ToString("G9", CultureInfo.InvariantCulture);

    /// <summary>
    /// Family A ladder: N nodes in a chain, series resistor per link, shunt
    /// resistor to ground per node, 1 A injected at node 0. This is the
    /// compacted long-cable shape.
    /// </summary>
    public static LinearSystem LadderA(int nodes, int seed = 1)
    {
        var rng = new Random(seed);
        var b = new Builder(nodes);
        var rhs = new double[nodes];
        rhs[0] = 1.0;

        var spice = new StringBuilder("I1 0 n1 DC 1\n");
        for (var i = 0; i < nodes; i++)
        {
            var shunt = 50.0 + 200.0 * rng.NextDouble();
            b.StampResistor(i, -1, shunt);
            spice.Append(CultureInfo.InvariantCulture, $"RG{i + 1} n{i + 1} 0 {Ohms(shunt)}\n");
            if (i + 1 < nodes)
            {
                var series = 0.5 + 5.0 * rng.NextDouble();
                b.StampResistor(i, i + 1, series);
                spice.Append(CultureInfo.InvariantCulture, $"RS{i + 1} n{i + 1} n{i + 2} {Ohms(series)}\n");
            }
        }

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"ladderA-{nodes}",
            Dimension = nodes,
            Pattern = pattern,
            Values = values,
            Rhs = rhs,
            SpiceNetlist = spice.ToString(),
            SpiceVariables = Enumerable.Range(1, nodes).Select(i => $"v(n{i})").ToArray(),
        };
    }

    /// <summary>Family A grid: side×side resistor mesh, corner injection, corner shunts.</summary>
    public static LinearSystem GridA(int side, int seed = 2)
    {
        var n = side * side;
        var rng = new Random(seed);
        var b = new Builder(n);
        var rhs = new double[n];
        rhs[0] = 1.0;

        var spice = new StringBuilder("I1 0 n1 DC 1\n");
        var r = 0;
        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                var i = y * side + x;
                if (x + 1 < side)
                {
                    var ohms = 1.0 + 10.0 * rng.NextDouble();
                    b.StampResistor(i, i + 1, ohms);
                    spice.Append(CultureInfo.InvariantCulture, $"RH{++r} n{i + 1} n{i + 2} {Ohms(ohms)}\n");
                }
                if (y + 1 < side)
                {
                    var ohms = 1.0 + 10.0 * rng.NextDouble();
                    b.StampResistor(i, i + side, ohms);
                    spice.Append(CultureInfo.InvariantCulture, $"RV{++r} n{i + 1} n{i + side + 1} {Ohms(ohms)}\n");
                }
            }
        // Ground the far corner through a modest shunt so the system is nonsingular.
        b.StampResistor(n - 1, -1, 10.0);
        spice.Append(CultureInfo.InvariantCulture, $"RG1 n{n} 0 10\n");

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"gridA-{side}x{side}",
            Dimension = n,
            Pattern = pattern,
            Values = values,
            Rhs = rhs,
            SpiceNetlist = spice.ToString(),
            SpiceVariables = Enumerable.Range(1, n).Select(i => $"v(n{i})").ToArray(),
        };
    }

    /// <summary>
    /// Family B ladder: like LadderA but every `sourceEvery` nodes carries a
    /// grounded voltage source (branch row with a zero diagonal). This is the
    /// shape that breaks pivot-free solvers.
    /// </summary>
    public static LinearSystem LadderB(int nodes, int sourceEvery = 50, int seed = 3)
    {
        var rng = new Random(seed);
        var sourceNodes = new List<int>();
        for (var i = 0; i < nodes; i += sourceEvery)
            sourceNodes.Add(i);

        var dim = nodes + sourceNodes.Count;
        var b = new Builder(dim);
        var rhs = new double[dim];

        var spice = new StringBuilder();
        for (var i = 0; i < nodes; i++)
        {
            var shunt = 50.0 + 200.0 * rng.NextDouble();
            b.StampResistor(i, -1, shunt);
            spice.Append(CultureInfo.InvariantCulture, $"RG{i + 1} n{i + 1} 0 {Ohms(shunt)}\n");
            if (i + 1 < nodes)
            {
                var series = 0.5 + 5.0 * rng.NextDouble();
                b.StampResistor(i, i + 1, series);
                spice.Append(CultureInfo.InvariantCulture, $"RS{i + 1} n{i + 1} n{i + 2} {Ohms(series)}\n");
            }
        }

        var variables = Enumerable.Range(1, nodes).Select(i => $"v(n{i})").ToList();
        for (var s = 0; s < sourceNodes.Count; s++)
        {
            var volts = 6.0 + 6.0 * rng.NextDouble();
            b.StampGroundedVoltageSource(sourceNodes[s], nodes + s, volts, rhs);
            spice.Append(CultureInfo.InvariantCulture, $"V{s + 1} n{sourceNodes[s] + 1} 0 DC {Ohms(volts)}\n");
            variables.Add($"i(v{s + 1})");
        }

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"ladderB-{nodes}+{sourceNodes.Count}src",
            Dimension = dim,
            Pattern = pattern,
            Values = values,
            Rhs = rhs,
            SpiceNetlist = spice.ToString(),
            SpiceVariables = variables.ToArray(),
        };
    }

    /// <summary>
    /// Family A random mesh: spanning tree plus extra chords, mimicking an
    /// organically-built base after compaction. No SPICE netlist (residual
    /// and cross-agreement carry correctness here).
    /// </summary>
    public static LinearSystem RandomMeshA(int nodes, int extraEdges, int seed = 4)
    {
        var rng = new Random(seed);
        var b = new Builder(nodes);
        var rhs = new double[nodes];
        rhs[rng.Next(nodes)] = 1.0;

        for (var i = 1; i < nodes; i++)
            b.StampResistor(i, rng.Next(i), 0.5 + 20.0 * rng.NextDouble());
        for (var e = 0; e < extraEdges; e++)
        {
            int a = rng.Next(nodes), c = rng.Next(nodes);
            if (a != c)
                b.StampResistor(a, c, 0.5 + 20.0 * rng.NextDouble());
        }
        for (var i = 0; i < nodes; i += 25)
            b.StampResistor(i, -1, 100.0);

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"meshA-{nodes}",
            Dimension = nodes,
            Pattern = pattern,
            Values = values,
            Rhs = rhs,
        };
    }

    /// <summary>
    /// Two-wire ladder (the SWER-alternative wiring model, 2026-07-05):
    /// every load has a supply and a return conductor; no inherent earth
    /// return. Node layout interleaves supply (2i) and return (2i+1).
    /// A floating source drives the pair; each device negative leaks to
    /// earth through a high resistance purely to keep the matrix grounded.
    /// `loads` loads → 2·loads + 1 unknowns.
    /// </summary>
    public static LinearSystem Ladder2W(int loads, int seed = 7)
    {
        var rng = new Random(seed);
        var dim = 2 * loads + 1;
        var b = new Builder(dim);
        var rhs = new double[dim];

        var spice = new StringBuilder("V1 s1 t1 DC 12\n");
        var variables = new List<string>();
        for (var i = 0; i < loads; i++)
        {
            variables.Add($"v(s{i + 1})");
            variables.Add($"v(t{i + 1})");
        }

        for (var i = 0; i < loads; i++)
        {
            int supply = 2 * i, ret = 2 * i + 1;

            var load = 50.0 + 200.0 * rng.NextDouble();
            b.StampResistor(supply, ret, load);
            spice.Append(CultureInfo.InvariantCulture, $"RL{i + 1} s{i + 1} t{i + 1} {Ohms(load)}\n");

            // Implicit high-resistance ground at the device negative.
            b.StampResistor(ret, -1, 1e6);
            spice.Append(CultureInfo.InvariantCulture, $"RE{i + 1} t{i + 1} 0 1e6\n");

            if (i + 1 < loads)
            {
                var supplySeries = 0.5 + 5.0 * rng.NextDouble();
                var returnSeries = 0.5 + 5.0 * rng.NextDouble();
                b.StampResistor(supply, supply + 2, supplySeries);
                b.StampResistor(ret, ret + 2, returnSeries);
                spice.Append(CultureInfo.InvariantCulture, $"RS{i + 1} s{i + 1} s{i + 2} {Ohms(supplySeries)}\n");
                spice.Append(CultureInfo.InvariantCulture, $"RT{i + 1} t{i + 1} t{i + 2} {Ohms(returnSeries)}\n");
            }
        }

        b.StampVoltageSource(0, 1, 2 * loads, 12.0, rhs);
        variables.Add("i(v1)");

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"ladder2W-{loads}",
            Dimension = dim,
            Pattern = pattern,
            Values = values,
            Rhs = rhs,
            SpiceNetlist = spice.ToString(),
            SpiceVariables = variables.ToArray(),
        };
    }

    /// <summary>
    /// Two-wire grid: supply and return meshes stacked, a load bridging the
    /// layers at every cell, floating source at one corner, high-resistance
    /// earth leak at every device negative. side² loads → 2·side² + 1 unknowns.
    /// </summary>
    public static LinearSystem Grid2W(int side, int seed = 8)
    {
        var cells = side * side;
        var dim = 2 * cells + 1;
        var rng = new Random(seed);
        var b = new Builder(dim);
        var rhs = new double[dim];

        int Supply(int cell) => 2 * cell;
        int Return(int cell) => 2 * cell + 1;

        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                var cell = y * side + x;
                b.StampResistor(Supply(cell), Return(cell), 50.0 + 200.0 * rng.NextDouble());
                b.StampResistor(Return(cell), -1, 1e6);
                if (x + 1 < side)
                {
                    b.StampResistor(Supply(cell), Supply(cell + 1), 1.0 + 10.0 * rng.NextDouble());
                    b.StampResistor(Return(cell), Return(cell + 1), 1.0 + 10.0 * rng.NextDouble());
                }
                if (y + 1 < side)
                {
                    b.StampResistor(Supply(cell), Supply(cell + side), 1.0 + 10.0 * rng.NextDouble());
                    b.StampResistor(Return(cell), Return(cell + side), 1.0 + 10.0 * rng.NextDouble());
                }
            }

        b.StampVoltageSource(Supply(0), Return(0), 2 * cells, 12.0, rhs);

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"grid2W-{side}x{side}",
            Dimension = dim,
            Pattern = pattern,
            Values = values,
            Rhs = rhs,
        };
    }

    /// <summary>Small, oracle-checked systems: correctness gate for every backend.</summary>
    public static IEnumerable<LinearSystem> VerificationSet()
    {
        yield return LadderA(5);
        yield return LadderA(100);
        yield return GridA(16);
        yield return LadderB(100, sourceEvery: 25);
        yield return RandomMeshA(500, 1000);
        yield return Ladder2W(50);   // oracle-checks the floating-source stamp
        yield return Grid2W(8);
    }

    /// <summary>Benchmark matrix: the sizes design.md cares about.</summary>
    public static IEnumerable<LinearSystem> BenchmarkSet()
    {
        yield return LadderA(100);   // typical VS island, post-compaction
        yield return LadderA(500);   // large VS island
        yield return GridA(32);      // 1024 nodes, meshy topology
        yield return LadderB(500);   // voltage sources / pivoting stress
        yield return LadderA(2000);  // dense-vs-sparse crossover probe
    }

    /// <summary>The Stationeers load-time case: build + first solve at 10k.</summary>
    public static LinearSystem ColdBuildCase() => LadderA(10_000);

    /// <summary>
    /// Frozen-pivot stress: same topology as LadderB (voltage sources every
    /// 50 nodes), but resistances drawn log-uniform across the ENTIRE legal
    /// conductance range (solver.md numerics: 1e-9..1e3 S). Different seeds
    /// give identical patterns with wildly different values — the shape of
    /// a switch flip under KLU-style refactorization.
    /// </summary>
    public static LinearSystem LadderExtreme(int nodes, int seed, int sourceEvery = 50)
    {
        var rng = new Random(seed);
        double LogUniformOhms() => Math.Pow(10.0, -3.0 + 12.0 * rng.NextDouble());

        var sourceNodes = new List<int>();
        for (var i = 0; i < nodes; i += sourceEvery)
            sourceNodes.Add(i);

        var dim = nodes + sourceNodes.Count;
        var b = new Builder(dim);
        var rhs = new double[dim];

        for (var i = 0; i < nodes; i++)
        {
            b.StampResistor(i, -1, LogUniformOhms());
            if (i + 1 < nodes)
                b.StampResistor(i, i + 1, LogUniformOhms());
        }
        for (var s = 0; s < sourceNodes.Count; s++)
            b.StampGroundedVoltageSource(sourceNodes[s], nodes + s, 12.0, rhs);

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"ladderX-{nodes}-s{seed}",
            Dimension = dim,
            Pattern = pattern,
            Values = values,
            Rhs = rhs,
        };
    }
}
