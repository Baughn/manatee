using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Manatee.Core.Diagnostics;

namespace Manatee.Core;

// SpiceDeck.Emit backing (api.md §22.c). Cold diagnostic path: walk one island's
// member components straight out of the document SoA and write a self-contained
// ngspice deck plus the rawfile-column ↔ handle maps a differ needs. Deterministic
// naming (arena slots) so the text is a stable Verify golden. Mirrors the numeric
// stamp path (BuildRuntime / StampComponent / StampWiring) so the deck is faithful
// to what the solver actually assembles — same datum choice, same wiring leaks,
// same switch/companion modeling resistances.
public sealed partial class Netlist
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal DeckResult EmitSpiceDeck(IslandId island, in SpiceEmitOptions opts)
    {
        var slot = island.Slot;
        if ((uint)slot >= (uint)_iSlotCount || !_iAlive[slot] || _iGen[slot] != island.Gen)
            throw new ArgumentException("IslandId does not name a live island of this netlist.", nameof(island));

        // Member node slots, ascending — the same order BuildRuntime assigns local
        // indices in, so the datum choice below matches the solver's.
        var nodes = new List<int>();
        for (var nn = 0; nn < _nCount; nn++)
            if (_nAlive[nn] && _nIsland[nn] == slot) nodes.Add(nn);

        // Datum (SPICE ground "0"): first Reference-role member, else the smallest
        // member slot — identical to BuildRuntime's refLocal/0 fallback.
        var datum = -1;
        foreach (var ns in nodes)
            if (_nRole[ns] == (byte)NodeRole.Reference) { datum = ns; break; }
        if (datum < 0 && nodes.Count > 0) datum = nodes[0];

        string Name(int ns) => ns == datum ? "0" : "n" + ns.ToString(Inv);

        var body = new StringBuilder();
        var models = new StringBuilder();
        var options = new StringBuilder();

        var unrep = new List<ComponentRef>();
        var branchNames = new List<(ComponentRef, string)>();
        var hasDiode = false;

        for (var c = 0; c < _cCount; c++)
        {
            if (!_cAlive[c] || _nIsland[_cA[c]] != slot) continue;
            var kind = (ComponentKind)_cKind[c];
            var a = _cA[c];
            var b = _cB[c];
            switch (kind)
            {
                case ComponentKind.Resistor:
                    body.Append("R").Append(c).Append(' ').Append(Name(a)).Append(' ').Append(Name(b))
                        .Append(' ').Append(Num(_cValue[c])).Append('\n');
                    break;

                case ComponentKind.Switch:
                    // Modeled exactly as the solver stamps it: a resistor at the
                    // closed/open modeling conductance (Circuit clamp reciprocals).
                    body.Append("R").Append(c).Append(' ').Append(Name(a)).Append(' ').Append(Name(b))
                        .Append(' ').Append(Num(_cValue[c] != 0.0 ? ClosedSwitchOhms : OpenSwitchOhms)).Append('\n');
                    break;

                case ComponentKind.VSource:
                    body.Append("V").Append(c).Append(' ').Append(Name(a)).Append(' ').Append(Name(b));
                    if (_cIsSine[c])
                    {
                        // ngspice SIN(VO VA FREQ TD THETA PHASE): PHASE is in DEGREES.
                        // Ours is radians (SineDrive.PhaseRad); the instantaneous value is
                        // amp·sin(2π·f·t + PhaseRad), so PHASE_deg = PhaseRad·180/π. DC value
                        // is 0 (the operating-point convention: sine sources sit at their DC
                        // offset). VO=0, TD=0, THETA=0.
                        var d = _cSine[c];
                        var phaseDeg = d.PhaseRad * 180.0 / Math.PI;
                        body.Append(" DC 0 SIN(0 ").Append(Num(d.AmplitudeV)).Append(' ')
                            .Append(Num(d.FreqHz)).Append(" 0 0 ").Append(Num(phaseDeg)).Append(')');
                    }
                    else
                    {
                        body.Append(" DC ").Append(Num(_cValue[c]));
                    }
                    body.Append('\n');
                    // ngspice exposes a V source's branch current as i(v{slot}), same sign
                    // convention as manatee's ReadFlow (a delivering source reads negative).
                    branchNames.Add((new ComponentRef(ComponentKind.VSource, c, _cGen[c], _netId), "i(v" + c.ToString(Inv) + ")"));
                    break;

                case ComponentKind.ISource:
                    // AddCurrentSource(from=a, to=b): −amps at from, +amps at to — identical
                    // to ngspice `I n+ n- value` (from = n+, to = n-).
                    body.Append("I").Append(c).Append(' ').Append(Name(a)).Append(' ').Append(Name(b))
                        .Append(" DC ").Append(Num(_cValue[c])).Append('\n');
                    break;

                case ComponentKind.Capacitor:
                    body.Append("C").Append(c).Append(' ').Append(Name(a)).Append(' ').Append(Name(b))
                        .Append(' ').Append(Num(_cValue[c])).Append(" ic=").Append(Num(_cStateVar[c])).Append('\n');
                    break;

                case ComponentKind.Inductor:
                    body.Append("L").Append(c).Append(' ').Append(Name(a)).Append(' ').Append(Name(b))
                        .Append(' ').Append(Num(_cValue[c])).Append(" ic=").Append(Num(_cStateVar[c])).Append('\n');
                    break;

                case ComponentKind.Diode:
                {
                    var mdl = "DMOD" + c.ToString(Inv);
                    // anode = a, cathode = b.
                    body.Append("D").Append(c).Append(' ').Append(Name(a)).Append(' ').Append(Name(b))
                        .Append(' ').Append(mdl).Append('\n');
                    var dp = _cDiode[c];
                    models.Append(".model ").Append(mdl).Append(" D(Is=").Append(Num(dp.SaturationCurrent))
                          .Append(" N=").Append(Num(dp.Emission)).Append(")\n");
                    hasDiode = true;
                    break;
                }

                case ComponentKind.IdealTransformer:
                    EmitTransformer(body, c, Name);
                    break;

                default:
                    unrep.Add(new ComponentRef(kind, c, _cGen[c], _netId));
                    break;
            }
        }

        // Construction-time wiring policy (StampWiring mirror): a leak / return
        // resistor from every Return-role node and every V-source negative to the
        // datum, exactly one per node (dedup). ExplicitOnly stamps nothing.
        EmitWiring(body, slot, datum, Name);

        // Diodes: pin ngspice's thermal voltage to the solver's fixed Vt(300 K) by
        // running at 26.85 °C = 300.00 K (the DiodeOracleTests convention).
        if (hasDiode)
            options.Append(".options temp=26.85 tnom=26.85\n");

        // Node-column and analysis assembly.
        var nodeNames = new List<(NodeId, string)>();
        foreach (var ns in nodes)
            if (ns != datum)
                nodeNames.Add((new NodeId(ns, _nGen[ns], _netId), "v(n" + ns.ToString(Inv) + ")"));

        var text = AssembleDeck(slot, body.ToString(), models.ToString(), options.ToString(), opts);
        return new DeckResult(text, unrep.ToArray(), nodeNames, branchNames);
    }

    // Ideal transformer as a VCVS + CCCS pair (api.md §6), reproducing the solver's
    // two coupled aux rows exactly:
    //   voltage: V(aPos)−V(aNeg) = n·(V(bPos)−V(bNeg))   [the E element]
    //   current: i_s = −n·i_p, i_p measured into aPos     [a 0 V ammeter + F element]
    // A per-transformer internal node carries the primary between the ammeter and E.
    private void EmitTransformer(StringBuilder body, int c, Func<int, string> name)
    {
        var aPos = name(_cA[c]);
        var aNeg = name(_cB[c]);
        var bPos = name(_cC[c]);
        var bNeg = name(_cD[c]);
        var n = Num(_cValue[c]);
        var tp = "tp" + c.ToString(Inv);   // internal primary node (ammeter → E output)

        // E: V(tp,aNeg) = n·(V(bPos)−V(bNeg)). VP is 0 V so V(aPos)=V(tp).
        body.Append("E").Append(c).Append(' ').Append(tp).Append(' ').Append(aNeg)
            .Append(' ').Append(bPos).Append(' ').Append(bNeg).Append(' ').Append(n).Append('\n');
        // VP: 0 V ammeter measuring i_p flowing into aPos (aPos → tp).
        body.Append("VP").Append(c).Append(' ').Append(aPos).Append(' ').Append(tp).Append(" DC 0\n");
        // F: secondary current source of value n·i_p. Oriented bNeg → bPos so the
        // reflected impedance is POSITIVE — matching the solver's i_s = −n·i_p current
        // constraint (verified against the loaded-transformer hand calc + ngspice: the
        // bPos→bNeg orientation flips the reflected-load sign and mismatches).
        body.Append("F").Append(c).Append(' ').Append(bNeg).Append(' ').Append(bPos)
            .Append(" VP").Append(c).Append(' ').Append(n).Append('\n');
    }

    private void EmitWiring(StringBuilder body, int islandSlot, int datum, Func<int, string> name)
    {
        if (_opts.Wiring.Kind == WiringPolicy.Mode.ExplicitOnly) return;
        var wireOhms = _opts.Wiring.Kind == WiringPolicy.Mode.TwoWireLeak
            ? _opts.Wiring.Parameter
            : 1.0 / _opts.Wiring.Parameter;

        var seen = new HashSet<int>();
        void Leak(int nodeSlot)
        {
            if (nodeSlot == datum || !seen.Add(nodeSlot)) return;
            body.Append("RW").Append(nodeSlot).Append(' ').Append(name(nodeSlot)).Append(" 0 ")
                .Append(Num(wireOhms)).Append('\n');
        }

        for (var nn = 0; nn < _nCount; nn++)
            if (_nAlive[nn] && _nIsland[nn] == islandSlot && _nRole[nn] == (byte)NodeRole.Return)
                Leak(nn);
        for (var c = 0; c < _cCount; c++)
            if (_cAlive[c] && (ComponentKind)_cKind[c] == ComponentKind.VSource && _nIsland[_cA[c]] == islandSlot)
                Leak(_cB[c]);
    }

    private static string AssembleDeck(int islandSlot, string body, string models, string options,
                                       in SpiceEmitOptions opts)
    {
        var sb = new StringBuilder();
        sb.Append("* manatee island ").Append(islandSlot).Append('\n');
        sb.Append(body);
        sb.Append(models);
        var tran = opts.Analysis.Kind == SpiceAnalysis.AnalysisKind.Tran;
        if (tran && opts.MatchBackwardEuler)
            // order-1 Gear ≡ Backward Euler; trtol=1e12 disables truncation-error step
            // refinement so ngspice honors the fixed print step exactly (below), instead
            // of subdividing internally — that refinement is what otherwise makes an
            // adaptive ngspice sinusoid diverge a few % from manatee's coarse fixed-step BE.
            sb.Append(".options method=gear maxord=1 trtol=1e12\n");
        sb.Append(options);
        sb.Append(".control\n");
        sb.Append("set filetype=ascii\n");
        if (!tran)
        {
            sb.Append("op\n");
        }
        else
        {
            sb.Append("tran ").Append(Num(opts.Analysis.Step)).Append(' ').Append(Num(opts.Analysis.Stop));
            // tstart=0, tmax=step: cap the timestep AT the print step so (with trtol above)
            // ngspice takes exactly one Backward-Euler step per print point — the matched-
            // timestep comparison testing-strategy.md calls for.
            if (opts.MatchBackwardEuler)
                sb.Append(" 0 ").Append(Num(opts.Analysis.Step));
            sb.Append(" uic\n");
        }
        sb.Append("write output.raw\n");
        sb.Append(".endc\n");
        sb.Append(".end\n");
        return sb.ToString();
    }

    // Round-trippable, culture-invariant number formatting: deterministic across
    // platforms so the deck text is a stable Verify golden.
    private static string Num(double d) => d.ToString("R", Inv);
}
