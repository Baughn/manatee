using Manatee.Core.Solver;

namespace Manatee.Core.Tests.Solver;

/// <summary>
/// A concrete MNA system A·x = b. Deterministic circuit-shaped generators
/// ported from <c>experiments/solver-bench/Circuits.cs</c> (the frozen
/// backend-competition harness). The SPICE-netlist companions are dropped —
/// this phase is matrices only; correctness rests on cross-agreement with the
/// naive dense referee, which the competition already audited against ngspice.
/// </summary>
internal sealed class LinearSystem
{
    public required string Name { get; init; }
    public required int Dimension { get; init; }
    public required MatrixEntry[] Pattern { get; init; }
    public required double[] Values { get; init; }
    public required double[] Rhs { get; init; }

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

    /// <summary>Reference scale for a relative residual: max |b|, floored at 1.</summary>
    public double Scale()
    {
        double s = 1.0;
        for (var i = 0; i < Rhs.Length; i++)
            s = Math.Max(s, Math.Abs(Rhs[i]));
        return s;
    }
}

/// <summary>
/// Deterministic MNA system generators (seeded RNG only — no wall-clock, no
/// ambient randomness). Family A: resistors + current sources ⇒ symmetric
/// positive definite. Family B: adds voltage-source branch rows ⇒ unsymmetric,
/// with structurally zero diagonals — the case that punishes naive pivoting.
/// </summary>
internal static class SystemFixtures
{
    private sealed class Builder
    {
        private readonly Dictionary<(int Row, int Col), double> _entries = new();
        public int Dimension { get; }

        public Builder(int dimension) => Dimension = dimension;

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

    /// <summary>
    /// Family A ladder: N nodes in a chain, series resistor per link, shunt
    /// resistor to ground per node, 1 A injected at node 0. The compacted
    /// long-cable shape.
    /// </summary>
    public static LinearSystem LadderA(int nodes, int seed = 1)
    {
        var rng = new Random(seed);
        var b = new Builder(nodes);
        var rhs = new double[nodes];
        rhs[0] = 1.0;

        for (var i = 0; i < nodes; i++)
        {
            b.StampResistor(i, -1, 50.0 + 200.0 * rng.NextDouble());
            if (i + 1 < nodes)
                b.StampResistor(i, i + 1, 0.5 + 5.0 * rng.NextDouble());
        }

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"ladderA-{nodes}", Dimension = nodes,
            Pattern = pattern, Values = values, Rhs = rhs,
        };
    }

    /// <summary>
    /// Family A ladder with EVERY conductance pinned to <paramref name="siemens"/>.
    /// Uniform scale keeps the matrix well-conditioned while placing all stamps at
    /// one point of the legal range [1e-9, 1e3] S — the axis for verifying the
    /// solver survives tiny and huge conductances without stiffness confounding it.
    /// </summary>
    public static LinearSystem LadderAtConductance(int nodes, double siemens)
    {
        var ohms = 1.0 / siemens;
        var b = new Builder(nodes);
        var rhs = new double[nodes];
        rhs[0] = 1.0;
        for (var i = 0; i < nodes; i++)
        {
            b.StampResistor(i, -1, ohms);
            if (i + 1 < nodes)
                b.StampResistor(i, i + 1, ohms);
        }
        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"ladderG-{nodes}@{siemens:E0}S", Dimension = nodes,
            Pattern = pattern, Values = values, Rhs = rhs,
        };
    }

    /// <summary>Family A grid: side×side resistor mesh, corner injection, corner shunt.</summary>
    public static LinearSystem GridA(int side, int seed = 2)
    {
        var n = side * side;
        var rng = new Random(seed);
        var b = new Builder(n);
        var rhs = new double[n];
        rhs[0] = 1.0;

        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                var i = y * side + x;
                if (x + 1 < side) b.StampResistor(i, i + 1, 1.0 + 10.0 * rng.NextDouble());
                if (y + 1 < side) b.StampResistor(i, i + side, 1.0 + 10.0 * rng.NextDouble());
            }
        // Ground the far corner through a modest shunt so the system is nonsingular.
        b.StampResistor(n - 1, -1, 10.0);

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"gridA-{side}x{side}", Dimension = n,
            Pattern = pattern, Values = values, Rhs = rhs,
        };
    }

    /// <summary>
    /// Family B ladder: like LadderA but every <paramref name="sourceEvery"/>
    /// nodes carries a grounded voltage source (branch row with a zero diagonal).
    /// The shape that breaks pivot-free solvers.
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

        for (var i = 0; i < nodes; i++)
        {
            b.StampResistor(i, -1, 50.0 + 200.0 * rng.NextDouble());
            if (i + 1 < nodes)
                b.StampResistor(i, i + 1, 0.5 + 5.0 * rng.NextDouble());
        }
        for (var s = 0; s < sourceNodes.Count; s++)
            b.StampGroundedVoltageSource(sourceNodes[s], nodes + s, 6.0 + 6.0 * rng.NextDouble(), rhs);

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"ladderB-{nodes}+{sourceNodes.Count}src", Dimension = dim,
            Pattern = pattern, Values = values, Rhs = rhs,
        };
    }

    /// <summary>
    /// Family A random mesh: spanning tree plus extra chords, mimicking an
    /// organically-built base after compaction.
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
            Name = $"meshA-{nodes}", Dimension = nodes,
            Pattern = pattern, Values = values, Rhs = rhs,
        };
    }

    /// <summary>
    /// Two-wire ladder (SWER-alternative wiring): every load has a supply and a
    /// return conductor; no inherent earth return. A floating source drives the
    /// pair; each device negative leaks to earth through a high resistance purely
    /// to keep the matrix grounded. <paramref name="loads"/> loads → 2·loads + 1
    /// unknowns. Exercises the floating-source branch stamp.
    /// </summary>
    public static LinearSystem Ladder2W(int loads, int seed = 7)
    {
        var rng = new Random(seed);
        var dim = 2 * loads + 1;
        var b = new Builder(dim);
        var rhs = new double[dim];

        for (var i = 0; i < loads; i++)
        {
            int supply = 2 * i, ret = 2 * i + 1;
            b.StampResistor(supply, ret, 50.0 + 200.0 * rng.NextDouble());
            b.StampResistor(ret, -1, 1e6); // implicit high-resistance ground at device negative
            if (i + 1 < loads)
            {
                b.StampResistor(supply, supply + 2, 0.5 + 5.0 * rng.NextDouble());
                b.StampResistor(ret, ret + 2, 0.5 + 5.0 * rng.NextDouble());
            }
        }
        b.StampVoltageSource(0, 1, 2 * loads, 12.0, rhs);

        var (pattern, values) = b.Build();
        return new LinearSystem
        {
            Name = $"ladder2W-{loads}", Dimension = dim,
            Pattern = pattern, Values = values, Rhs = rhs,
        };
    }

    /// <summary>
    /// Two-wire grid: supply and return meshes stacked, a load bridging the
    /// layers at every cell, floating source at one corner, high-resistance earth
    /// leak at every device negative. side² loads → 2·side² + 1 unknowns.
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
            Name = $"grid2W-{side}x{side}", Dimension = dim,
            Pattern = pattern, Values = values, Rhs = rhs,
        };
    }

    /// <summary>
    /// Frozen-pivot stress shape: same topology as <see cref="LadderB"/> (voltage
    /// sources every <paramref name="sourceEvery"/> nodes), but resistances drawn
    /// log-uniform across the FULL legal conductance range (solver.md numerics:
    /// 1e-9..1e3 S, i.e. 1e-3..1e9 Ω). Different seeds give identical patterns
    /// with wildly different values — a maximal tier-2 swing on one frozen pattern,
    /// the shape of a switch flip under KLU-style refactorization.
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
            Name = $"ladderX-{nodes}-s{seed}", Dimension = dim,
            Pattern = pattern, Values = values, Rhs = rhs,
        };
    }
}
