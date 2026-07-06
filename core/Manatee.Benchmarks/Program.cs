using System;
using System.Linq;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// Standing suites (testing-strategy.md Benchmarks). Real runs use the default
// out-of-process toolchain:
//   dotnet run -c Release --project core/Manatee.Benchmarks -- --filter '*LadderDcTick*'
//
// `--smoke` runs one in-process Dry iteration of the matched suites — the fast "does it
// start and produce a measurement" check. In-process avoids BDN regenerating+building a
// child project (which trips over the .direnv flake-source snapshot in this dev tree);
// it is verification-only, not the accurate measurement path.
//
// CAVEAT — smoke's Allocated column is COLD-START NOISE, not a verdict: Job.Dry runs a
// single op, so first-call JIT / lazy-buffer allocations land inside the measured
// iteration and the "zero bytes after warmup" suites can show a few hundred B at larger
// N. The authoritative 0 B gates are the [Fact]s in Tests/Clients/TierBudgetGateTests
// (min over 8×200 warmed iterations) and MemoryDiagnoser under the default
// out-of-process toolchain. Do not "fix" a nonzero smoke Allocated reading.
var smoke = args.Contains("--smoke");
var passthrough = args.Where(a => a != "--smoke").ToArray();

IConfig? config = null;
if (smoke)
    config = ManualConfig.Create(DefaultConfig.Instance)
        .AddJob(Job.Dry.WithToolchain(InProcessEmitToolchain.Instance))
        .AddDiagnoser(MemoryDiagnoser.Default);

BenchmarkSwitcher
    .FromAssembly(typeof(Manatee.Benchmarks.LadderDcTickBenchmarks).Assembly)
    .Run(passthrough, config);
