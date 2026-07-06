using System;
using System.Buffers.Binary;

namespace Manatee.Core.Devices;

/// <summary>
/// A swing-equation-lite synchronous alternator (api.md §18; solver.md Component
/// Set). The rotor carries two state variables — angle θ and angular velocity ω —
/// evolved by a spring/damper swing equation from the mechanical inputs (shaft
/// speed, available torque, both <see cref="Tick"/> parameters) against the
/// ELECTRICAL counter-torque read back from the actual solve. It drives a sine
/// voltage source whose FREQUENCY is <c>ω·polePairs / 2π</c> and whose AMPLITUDE is
/// the back-EMF <c>k·ω</c> (a real machine's terminal voltage rises with speed).
///
/// <para><b>Paralleling / phase-lock (documented coupling math).</b> Put two
/// alternators on one bus. Machine i sources current <c>I_i ≈ (E_i − V_bus)/R</c>
/// with <c>E_i = k·ω_i</c>. The one spinning faster has the larger back-EMF, so it
/// sources MORE current and absorbs MORE electrical counter-torque
/// <c>T_e = P_elec/ω</c>, which decelerates it; the slower machine sees
/// <c>E_i &lt; V_bus</c>, draws current (motors), and accelerates. The speed
/// difference therefore obeys a damped first-order law
/// <c>Δω̇ ≈ −(2k²/RM + D/M)·Δω</c> and decays to a bounded value; since
/// <c>θ̇ = ω</c>, the rotor-angle difference <c>Δθ</c> converges to a bounded
/// constant. This is the synchronising-power mechanism reduced to its quasi-static
/// core — the "lite" swing equation — and it is what the phase-lock property test
/// asserts (start out of phase, converge to a bounded Δθ, never strobe).</para>
///
/// <para><b>Component ordinals (api.md §18):</b> 0 = the sine EMF source, 1 = the
/// internal series (synchronous) resistance, 2 = the internal EMF node. Terminals:
/// 0 = positive (output) port, 1 = negative (return) port. The sine source carries
/// its OWN state unit (phase accumulator) under <c>StateKey.From(baseKey.Derive(0))</c>,
/// distinct from the rotor state under the device's StateKey.</para>
/// </summary>
public sealed class Alternator : Device
{
    private const double TwoPi = 2.0 * Math.PI;
    private const double OmegaFloor = 1e-3;   // avoid divide-by-zero in T_e = P/ω

    // ── config (cold) ──
    private readonly double _inertiaM;     // rotor inertia (swing mass)
    private readonly double _damping;      // mechanical damping D
    private readonly double _polePairs;
    private readonly double _emfPerOmega;  // back-EMF constant k (V per rad/s)
    private readonly double _seriesOhms;
    private readonly double _govGain;      // governor: torque per rad/s of speed error

    // ── built handles ──
    private VSourceId _sine;
    private ResistorId _rr;
    private NodeId _pos, _neg, _mid;

    // ── serialized rotor state ──
    private double _omega;   // rotor angular velocity [rad/s]
    private double _theta;   // rotor angle [rad]

    // ── per-tick inputs / readback ──
    private double _shaftSpeed;
    private double _availTorque;
    private double _counterTorque;

    /// <param name="inertia">Rotor inertia M in the swing equation (&gt; 0).</param>
    /// <param name="damping">Mechanical damping D.</param>
    /// <param name="polePairs">Electrical pole pairs (electrical freq = ω·polePairs/2π).</param>
    /// <param name="emfPerOmega">Back-EMF constant k: sine amplitude = k·ω.</param>
    /// <param name="seriesOhms">Internal (synchronous) series resistance.</param>
    /// <param name="governorGain">Prime-mover governor gain toward the shaft speed.</param>
    /// <param name="initialOmega">Initial rotor speed.</param>
    /// <param name="initialTheta">Initial rotor angle (phase-lock tests start these apart).</param>
    public Alternator(double inertia, double damping, double polePairs, double emfPerOmega,
        double seriesOhms, double governorGain = 0.0, double initialOmega = 0.0, double initialTheta = 0.0)
    {
        if (!(inertia > 0.0)) throw new ArgumentOutOfRangeException(nameof(inertia));
        if (!(seriesOhms > 0.0)) throw new ArgumentOutOfRangeException(nameof(seriesOhms));
        _inertiaM = inertia; _damping = damping; _polePairs = polePairs;
        _emfPerOmega = emfPerOmega; _seriesOhms = seriesOhms; _govGain = governorGain;
        _omega = initialOmega; _theta = initialTheta;
        _shaftSpeed = initialOmega;
    }

    public override TerminalSpec Terminals => new(2);

    /// <summary>Rotor angular velocity [rad/s].</summary>
    public double Omega => _omega;

    /// <summary>Rotor angle [rad] (unwrapped; the phase-lock observable).</summary>
    public double Theta => _theta;

    /// <summary>Electrical frequency the machine is presenting [Hz].</summary>
    public double FrequencyHz => _omega * _polePairs / TwoPi;

    /// <summary>Last electrical counter-torque read back from the solve.</summary>
    public double CounterTorque => _counterTorque;

    /// <summary>Set the mechanical drive for the coming tick: the governor pulls the
    /// rotor toward <paramref name="shaftSpeed"/> and <paramref name="availableTorque"/>
    /// is the prime mover's feed-forward torque.</summary>
    public void SetMechanical(double shaftSpeed, double availableTorque)
    {
        _shaftSpeed = shaftSpeed;
        _availTorque = availableTorque;
    }

    protected override void Build(StructuralEdit e, ReadOnlySpan<NodeId> terminals,
                                  in ExternalKey baseKey, in StateKey state)
    {
        _pos = terminals[0];
        _neg = terminals[1];
        _mid = e.AddNode(baseKey.Derive(2));                                     // ordinal 2 (internal node)
        var f0 = _omega * _polePairs / TwoPi;
        var amp0 = _emfPerOmega * _omega;
        _sine = e.AddSineSource(_mid, _neg, new SineDrive(amp0, f0, 0.0),
            baseKey.Derive(0), StateKey.From(baseKey.Derive(0)));          // ordinal 0
        _rr = e.AddResistor(_mid, _pos, _seriesOhms, baseKey.Derive(1));         // ordinal 1
    }

    public override void Tick(in DeviceTickContext ctx)
    {
        var dt = ctx.Dt;

        // Electrical counter-torque from the actual delivered power. Power ABSORBED by
        // the source (a→b convention) is negative when it delivers, so negate to get
        // the delivered electrical power; T_e = P_elec / ω.
        var pDelivered = -ctx.Previous.Power(_sine);
        var wAbs = _omega < 0.0 ? -_omega : _omega;
        var tE = wAbs > OmegaFloor ? pDelivered / _omega : 0.0;
        _counterTorque = tE;

        // Swing equation (lite): governor pulls toward the shaft speed; the electrical
        // counter-torque and mechanical damping oppose. M·ω̇ = T_avail + gov·(ω_shaft−ω) − T_e − D·ω.
        var tMech = _availTorque + _govGain * (_shaftSpeed - _omega);
        var domega = (tMech - tE - _damping * _omega) / _inertiaM;
        _omega += domega * dt;
        _theta += _omega * dt;

        // Drive the sine: frequency ∝ speed, amplitude = back-EMF k·ω (phase-continuous).
        var f = _omega * _polePairs / TwoPi;
        var amp = _emfPerOmega * _omega;
        ctx.Drive(_sine, new SineDrive(amp, f, 0.0));
    }

    public override int StateSize => 16;

    public override void SaveState(Span<byte> dst)
    {
        BinaryPrimitives.WriteInt64LittleEndian(dst, BitConverter.DoubleToInt64Bits(_omega));
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(8), BitConverter.DoubleToInt64Bits(_theta));
    }

    public override void RestoreState(ReadOnlySpan<byte> src)
    {
        _omega = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(src));
        _theta = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(src.Slice(8)));
    }
}
