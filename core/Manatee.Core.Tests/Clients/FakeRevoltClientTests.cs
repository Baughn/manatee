using System;
using Manatee.Core;

namespace Manatee.Core.Tests.Clients;

/// <summary>
/// Drives <see cref="FakeRevoltClient"/> through the §22.a scenarios (api.md §22.a;
/// testing-strategy.md). The through-line is the standing telemetry contract: steady
/// ticks refactorize zero times, and StaleHandleReads is zero on EVERY tick — the re-pin
/// discipline (stable quiet AdaptedLoads + key-re-pinned active adaptors) must hold under
/// breaker trips, cable cuts, device churn, a ring overflow, and a Faulted island.
/// </summary>
[Collection(Manatee.Core.Tests.ZeroAllocCollection.Name)]   // serialized: the drive phase runs under an armed
                                                            // AllocationSentinel, and the per-thread GC counter reads
                                                            // phantom bytes (~8 KB quantum) under sibling-test
                                                            // compaction/JIT churn (see ZeroAllocCollection)
public sealed class FakeRevoltClientTests
{
    // Warm the fleet until the constant-power loads converge (Adjust ε-no-ops).
    private static FakeRevoltClient Warmed(int ticks = 60)
    {
        var c = new FakeRevoltClient();
        for (var t = 0; t < ticks; t++) c.Tick();
        return c;
    }

    [Fact]
    public void Base_compacts_and_carries_the_expected_load_fleet()
    {
        var c = new FakeRevoltClient();
        Assert.Equal(80, c.AdaptedLoadCount);                 // the built-in AdaptedLoad fleet
        Assert.True(c.ActiveLoadCount > 0);                   // plus the re-pinnable active adaptors
        Assert.True(c.LiveNodeCount < 1000, $"active node count {c.LiveNodeCount} not collapsed");

        for (var n = 0; n < 34; n++) Assert.True(c.QuietNetworkIsLive(n), $"quiet network {n} not live");
        for (var n = 34; n < FakeRevoltClient.NetworkCount; n++) Assert.True(c.ActiveNetworkIsLive(n), $"active network {n} not live");
    }

    [Fact]
    public void Steady_tick_does_not_refactorize_rebuild_or_read_stale()
    {
        var c = Warmed();
        c.Tick();
        Assert.Equal(0, c.LastRefactorizations);
        Assert.Equal(0, c.LastRebuilds);
        Assert.Equal(0, c.TotalStaleReads);
        Assert.InRange(c.AdaptedLoadPower(0, 0), 150.0, 260.0);   // settled near advertised 200 W
        Assert.True(c.LastApplyDisjoint);
        Assert.True(c.LastApplyCount >= c.AdaptedLoadCount);
    }

    [Fact]
    public void Breaker_close_merges_partitions_then_open_splits_them()
    {
        var c = Warmed();
        Assert.NotEqual(c.ActiveIsland(34).Id, c.ActiveIsland(35).Id);

        c.QueueBreaker(34, CouplerState.Closed);
        c.Tick();
        Assert.Equal(c.ActiveIsland(34).Id, c.ActiveIsland(35).Id);            // merged
        Assert.Equal(PartitionKey.None, c.ActiveIsland(34).Partition);         // spans two partitions

        c.QueueBreaker(34, CouplerState.Open);
        c.Tick();
        Assert.NotEqual(c.ActiveIsland(34).Id, c.ActiveIsland(35).Id);         // split again
        Assert.Equal(0, c.TotalStaleReads);
    }

    [Fact]
    public void Cable_cut_rebuilds_the_run_and_repins_active_loads()
    {
        var c = Warmed();
        c.QueueCut(36, 380);                 // a far-end segment (past every load tap)
        c.Tick();
        for (var t = 0; t < 3; t++) c.Tick();
        Assert.True(c.Cuts >= 1);
        Assert.True(c.ActiveNetworkIsLive(36));
        Assert.Equal(0, c.TotalStaleReads);
    }

    [Fact]
    public void Skipping_the_post_solve_repin_on_a_churn_tick_reads_stale()
    {
        // Proves two things at once (2026-07-06 adjudication): (1) the client's
        // post-Solve re-pin is LOAD-BEARING, not ritual — the island rebuild runs
        // inside Solve and reissues member handles, so an ApplyState readback without
        // the re-pin dereferences exactly the handles this tick's Solve invalidated;
        // and (2) the StaleHandleReads counter is a live tripwire on this device
        // model, so the zero asserted on every other tick is a real measurement.
        var c = Warmed();
        c.PostSolveRepin = false;
        c.QueueCut(36, 380);
        c.Tick();
        Assert.True(c.LastRebuilds >= 1, "the cut tick must rebuild the island");
        Assert.True(c.TotalStaleReads > 0,
            "a churn tick without the post-solve re-pin must trip the stale-read counter");

        // The next tick's pre-drive re-pin (DrainChanges reports the rebuild) recovers.
        c.PostSolveRepin = true;
        var before = c.TotalStaleReads;
        c.Tick();
        Assert.Equal(before, c.TotalStaleReads);
        Assert.True(c.ActiveNetworkIsLive(36));
    }

    [Fact]
    public void Device_add_and_remove_mid_run()
    {
        var c = Warmed();
        var before = c.ActiveLoadCount;
        var added = c.QueueAddAdaptor(37, tapIndex: 45, slot: 0, watts: 150.0);
        c.Tick(); c.Tick();
        Assert.Equal(before + 1, c.ActiveLoadCount);

        c.QueueRemoveAdaptor(added);
        c.Tick(); c.Tick();
        Assert.Equal(before, c.ActiveLoadCount);
        Assert.Equal(0, c.TotalStaleReads);
    }

    [Fact]
    public void Ring_overflow_forces_a_full_repin()
    {
        var c = Warmed();
        var before = c.FullRepins;
        c.QueueOverflow(320);
        c.Tick();
        Assert.True(c.LastDrainLost, "a 320-network bulk build should overflow the change ring");
        Assert.True(c.FullRepins > before, "a lost drain must force at least one full re-pin");
        Assert.Equal(0, c.TotalStaleReads);
        Assert.True(c.QuietNetworkIsLive(0));
    }

    [Fact]
    public void Faulted_island_falls_back_and_then_recovers()
    {
        var c = Warmed();
        c.QueueInduceFault();
        c.Tick();
        Assert.Equal(IslandStatus.Faulted, c.FaultIslandStatus());
        Assert.True(c.ScalarFallbacks >= 1);            // the vanilla scalar fallback ran
        Assert.True(c.QuietNetworkIsLive(0));           // neighbours keep solving
        Assert.True(c.ActiveNetworkIsLive(34));

        c.QueueClearFault();
        c.Tick(); c.Tick();
        Assert.NotEqual(IslandStatus.Faulted, c.FaultIslandStatus());   // recovered
        Assert.Equal(0, c.TotalStaleReads);
    }

    [Fact]
    public void Quiet_network_browns_out_and_rejoins()
    {
        var c = Warmed();
        Assert.Equal(0, c.AdaptedLoadMode(0, 0));       // Live

        c.DriveSource(0, 20.0);                          // collapse the feeder below V_low = 50
        for (var t = 0; t < 6; t++) c.Tick();
        Assert.Equal(1, c.AdaptedLoadMode(0, 0));        // BrownedOut

        c.DriveSource(0, 120.0);
        for (var t = 0; t < 14; t++) c.Tick();
        Assert.Equal(0, c.AdaptedLoadMode(0, 0));        // Live again after the stagger delay
        Assert.Equal(0, c.TotalStaleReads);
    }

    [Fact]
    public void Save_blob_restore_across_a_merged_boundary_is_additive()
    {
        var c = Warmed();
        var blob34 = c.SnapshotActive(34);
        var blob35 = c.SnapshotActive(35);
        var n34 = c.ActiveLoadsOf(34).Count;
        var n35 = c.ActiveLoadsOf(35).Count;
        Assert.True(n34 > 0 && n35 > 0);

        c.QueueBreaker(34, CouplerState.Closed);         // merge 34+35 since the save
        for (var t = 0; t < 3; t++) c.Tick();

        var r34 = c.RestoreActive(34, blob34);           // additive: matches only 34's StateKey'd units
        Assert.Equal(n34, r34.Matched);
        Assert.Equal(0, r34.OrphansInBlob);
        var r35 = c.RestoreActive(35, blob35);
        Assert.Equal(n35, r35.Matched);
        Assert.Equal(0, r35.OrphansInBlob);
        Assert.Equal(0, c.TotalStaleReads);
    }

    [Fact]
    public void Long_mixed_run_keeps_the_telemetry_contract_every_tick()
    {
        var c = Warmed(40);
        long refactorAfterQuietTicks = 0;
        for (var t = 0; t < 300; t++)
        {
            switch (t)
            {
                case 20: c.QueueBreaker(34, CouplerState.Closed); break;
                case 60: c.QueueCut(38, 370); break;
                case 90: c.QueueBreaker(34, CouplerState.Open); break;
                case 120: c.QueueOverflow(300); break;
                case 160: c.QueueInduceFault(); break;
                case 190: c.QueueClearFault(); break;
                case 220: c.QueueBreaker(35, CouplerState.Closed); break;
                case 250: c.QueueBreaker(35, CouplerState.Open); break;
            }
            c.Tick();

            Assert.Equal(0, c.TotalStaleReads);                           // zero on EVERY tick
            Assert.True(c.LastApplyDisjoint, $"tick {t}: ApplyState touched a device twice");
            if (t > 270) refactorAfterQuietTicks += c.LastRefactorizations;   // long-settled tail
        }
        Assert.Equal(0, refactorAfterQuietTicks);

        var inv = c.QuietIsland(0).CheckInvariants(InvariantChecks.Kcl | InvariantChecks.Finiteness);
        Assert.True(inv.AllFinite);
        Assert.True(inv.MaxKclResidual < 1e-6, $"KCL residual {inv.MaxKclResidual:R}");
    }
}
