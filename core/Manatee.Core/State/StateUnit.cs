using System;

namespace Manatee.Core.State;

/// <summary>
/// The kind tag stamped on every serializable state unit in a snapshot blob
/// (api.md §14). The four built-in kinds are the solver primitives that carry
/// dynamic state; <see cref="Device"/> is the phase-8 plug point (a whole device
/// serialises one opaque blob through <see cref="IDeviceStateUnit"/>).
/// </summary>
public enum StateUnitKind : byte
{
    /// <summary>Capacitor: stored node-pair voltage (Backward-Euler history).</summary>
    Capacitor = 1,

    /// <summary>Inductor: stored branch current (Backward-Euler history).</summary>
    Inductor = 2,

    /// <summary>Sine voltage source: phase accumulator (radians), phase-continuous.</summary>
    SinePhase = 3,

    /// <summary>Diode: persisted junction voltage (Newton warm start).</summary>
    Diode = 4,

    /// <summary>Phase-8 device blob (registered via <see cref="IDeviceStateUnit"/>).</summary>
    Device = 5,
}

/// <summary>
/// THE phase-8 plug point (api.md §14, solver.md State). A behavioural device
/// (battery state-of-charge integrator, generator rotor angle, converter
/// controller) that owns dynamic state its <c>Tick</c> evolves registers one of
/// these with <see cref="Netlist.RegisterDeviceState"/> so its state joins the
/// snapshot/restore stream keyed by <see cref="StateKey"/> — exactly as the
/// built-in capacitor/inductor/sine units do.
///
/// <para>The contract is deliberately minimal and blob-shaped: the core never
/// interprets the bytes, it only routes them by <see cref="StateKey"/>. This is
/// what keeps restore ADDITIVE and composable across topology drift (api.md §14):
/// a device's blob restores its own key and nothing else, so a merge/split
/// between save and load offers the same blob to each resulting island and each
/// takes only its matching units.</para>
///
/// <para><see cref="BlobSize"/> must be STABLE for a given unit between the
/// topology-changing ops that re-read <see cref="IslandHandle.SnapshotSize"/> —
/// it is summed into the island's snapshot size up front. <see cref="Save"/> must
/// write exactly <see cref="BlobSize"/> bytes; <see cref="Restore"/> reads exactly
/// that many. For law-4 bit-for-bit round-trip a device must serialise ALL state
/// that influences a future <c>Tick</c>/solve.</para>
/// </summary>
public interface IDeviceStateUnit
{
    /// <summary>The client-stable identity this unit's blob is keyed on.</summary>
    StateKey Key { get; }

    /// <summary>Fixed serialised size in bytes (stable between topology ops).</summary>
    int BlobSize { get; }

    /// <summary>Write exactly <see cref="BlobSize"/> bytes of state.</summary>
    void Save(Span<byte> dst);

    /// <summary>Read exactly <see cref="BlobSize"/> bytes back (a matched restore).</summary>
    void Restore(ReadOnlySpan<byte> src);
}
