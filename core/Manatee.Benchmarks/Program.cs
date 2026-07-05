using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Manatee.Benchmarks.PlaceholderBenchmarks).Assembly).Run(args);
