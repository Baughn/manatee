using System;
using System.Collections.Generic;
using System.Linq;
using Manatee.Core;
using Manatee.Core.Falstad;
using Manatee.Oracle;

namespace Manatee.Core.Tests.Lessons;

/// <summary>
/// The lesson corpus as CI goldens (testing-strategy.md "The Lesson Corpus as
/// Goldens"; design.md R20). Every lesson is checked two ways, discovered at test
/// time so a new lesson directory is picked up with no code change:
///
/// <para><b>Narrative pass</b> (fast) — import the netlist, solve, and assert each
/// front-matter expectation (probe → node voltage within tolerance) against the
/// manatee solution. A lesson that stops being true fails the build.</para>
///
/// <para><b>Oracle pass</b> (<c>Category=Oracle</c>) — import the netlist, emit a
/// SPICE deck, run ngspice through <see cref="OracleHarness"/>, and diff every node
/// (and V-source branch current) manatee-vs-ngspice.</para>
///
/// <para>Each pass also enforces the corpus lint that every expectation's
/// <c>probe:[x,y]</c> matches EXACTLY one <c>O</c> element.</para>
/// </summary>
public sealed class LessonCorpusTests
{
    public static IEnumerable<object[]> AllLessons()
        => LessonCorpus.Discover().Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(AllLessons))]
    public void Narrative_expectations_hold(string name)
    {
        var lesson = LessonCorpus.Load(name);
        var res = FalstadImporter.Import(lesson.CircuitText);
        AssertProbeLint(res, lesson);

        SolveTo(res, lesson, out var sampleAt);

        foreach (var exp in lesson.Expectations)
        {
            sampleAt(exp);
            Assert.True(res.TryGetProbe(exp.ProbeX, exp.ProbeY, out var probe),
                $"{lesson.Name}: no probe at [{exp.ProbeX},{exp.ProbeY}] for '{exp.Name}'.");
            var v = res.Netlist.Solution.Voltage(probe.Node);
            Assert.True(Math.Abs(v - exp.Value) <= exp.Tol,
                $"{lesson.Name} / {exp.Name}: expected {exp.Value} ± {exp.Tol} V, got {v} V.");
        }
    }

    [Theory]
    [MemberData(nameof(AllLessons))]
    [Trait("Category", "Oracle")]
    public void Oracle_agrees_with_ngspice(string name)
    {
        var lesson = LessonCorpus.Load(name);
        var res = FalstadImporter.Import(lesson.CircuitText);
        AssertProbeLint(res, lesson);
        Assert.True(res.HasReference, $"{lesson.Name}: lesson circuit has no ground/reference.");

        var island = res.Netlist.IslandOf(res.ReferenceNode);

        if (!lesson.IsTransient)
        {
            res.Netlist.SolveOperatingPoint();
            OracleHarness.AssertDcMatches(res.Netlist, island);
        }
        else
        {
            var dt = res.HeaderTimeStep ?? throw new InvalidOperationException(
                $"{lesson.Name}: transient lesson has no $ header timestep.");
            var stop = lesson.Stop ?? lesson.Expectations.Max(e => e.Time ?? 0.0);
            var steps = (int)Math.Round(stop / dt);
            OracleHarness.AssertTranMatches(
                res.Netlist, island, dt, stop,
                stepManatee: () => { for (var i = 0; i < steps; i++) res.Netlist.Solve(new TickClock(i, dt)); },
                relTol: 5e-3);
        }
    }

    private static void AssertProbeLint(FalstadImportResult res, Lesson lesson)
    {
        foreach (var exp in lesson.Expectations)
        {
            var n = res.CountProbesAt(exp.ProbeX, exp.ProbeY);
            Assert.True(n == 1,
                $"{lesson.Name} / {exp.Name}: probe [{exp.ProbeX},{exp.ProbeY}] matched {n} O elements (want exactly 1).");
        }
    }

    // Solve the netlist and hand back a per-expectation sampler. DC solves once and
    // the sampler is a no-op; transient steps forward (Backward Euler at the header
    // dt) to each expectation's time before it is read. Expectations must be sampled
    // in nondecreasing time order for the incremental stepping to be correct, so the
    // caller iterates them in file order (the schema authors them ascending).
    private static void SolveTo(FalstadImportResult res, Lesson lesson, out Action<Expectation> sampleAt)
    {
        var net = res.Netlist;
        if (!lesson.IsTransient)
        {
            net.SolveOperatingPoint();
            sampleAt = _ => { };
            return;
        }

        var dt = res.HeaderTimeStep ?? throw new InvalidOperationException(
            $"{lesson.Name}: transient lesson has no $ header timestep.");
        var stepped = 0;
        sampleAt = exp =>
        {
            var target = (int)Math.Round((exp.Time ?? 0.0) / dt);
            while (stepped < target)
            {
                net.Solve(new TickClock(stepped, dt));
                stepped++;
            }
        };
    }
}
