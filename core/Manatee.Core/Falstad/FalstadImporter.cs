using System;
using System.Collections.Generic;
using System.Globalization;

namespace Manatee.Core.Falstad;

/// <summary>
/// Imports a Falstad / circuitjs1 classic-text netlist into a manatee
/// <see cref="Netlist"/> (falstad-format.md is the spec). Two phases, in order:
///
/// <para><b>1. Parse + validate (no mutation).</b> Every line is tokenized on the
/// StringTokenizer delimiter set <c>" +\t\n\r\f"</c> and dispatched on its first
/// token. Supported elements are staged into an in-memory model; unsupported dump
/// types, malformed lines, and unrepresentable parameters accumulate as
/// <see cref="FalstadRejection"/>s. If ANY rejection is collected, the whole import
/// throws <see cref="FalstadImportException"/> — so a rejected import never leaves a
/// half-built document (the accept/reject contract, falstad-format.md §7).</para>
///
/// <para><b>2. Build.</b> Coordinates are unioned into electrical nodes (coincident
/// posts and <c>w</c> wires merge; a Falstad circuit has no explicit node list), a
/// single reference node is chosen (the unified <c>g</c> class), and the model is
/// replayed onto one <see cref="StructuralEdit"/>.</para>
///
/// <para><b>Deterministic key minting</b> (restore/drift depend on it,
/// falstad-format.md §7):
/// <list type="bullet">
/// <item><b>Node keys</b> are coordinate-derived: <c>ExternalKey(1, pack(x,y))</c>
/// of the node's CANONICAL coordinate — the lexicographically smallest
/// <c>(x,y)</c> in its union class. Order-independent: reordering or renumbering
/// element lines leaves node keys fixed, which is what lets the lesson corpus
/// address probes by coordinate.</item>
/// <item><b>Component keys</b> are <c>ExternalKey(2, elementOrdinal)</c> where the
/// ordinal is the element's 0-based position among ALL element lines in file order.
/// Unique (parallel duplicates that share endpoints still differ) and deterministic;
/// order-sensitive by construction — components are not coordinate-addressable.</item>
/// <item><b>Probe keys</b> are <c>ExternalKey(3, elementOrdinal)</c>; a series-R
/// synthesized node (none today) would take <c>ExternalKey(1, …)</c>.</item>
/// <item><b>StateKeys</b> for stateful primitives reuse the component key bits via
/// <see cref="StateKey.From(in ExternalKey)"/>.</item>
/// </list></para>
/// </summary>
public static class FalstadImporter
{
    private const ulong NodeKeyTag = 1;
    private const ulong CompKeyTag = 2;
    private const ulong ProbeKeyTag = 3;

    // Falstad voltage-source waveform ids (VoltageElm.java).
    private const int WfDc = 0;
    private const int WfAc = 1;

    /// <summary>Import <paramref name="text"/>. <paramref name="options"/> overrides
    /// the default profile; when null, the Tablet profile is used (falstad-format.md
    /// §7) with its tick dt taken from the <c>$</c> header's <c>maxTimeStep</c> when
    /// present.</summary>
    /// <exception cref="FalstadImportException">One or more lines were rejected.</exception>
    public static FalstadImportResult Import(string text, NetlistOptions? options = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var parser = new Parser(text);
        parser.Run();
        if (parser.Rejections.Count > 0)
            throw new FalstadImportException(parser.Rejections);
        return parser.Build(options);
    }

    // ─────────────────────────────────────────────────────────────── model

    private enum PendingKind : byte { Resistor, VSource, Sine, ISource, Capacitor, Inductor, Switch, Diode }

    private struct Pending
    {
        public PendingKind Kind;
        public int Ax, Ay, Bx, By;     // electrical terminal coordinates (post1 = A, post2 = B)
        public bool BIsGround;         // rail: B terminal is the reference node
        public double Value;           // R ohms / V volts / I amps / C farads / L henries / switch 0|1
        public DiodeParams Diode;
        public SineDrive Sine;
        public int Ordinal;            // element ordinal → component key
    }

    private struct PendingProbe
    {
        public int X, Y;               // first post = measured node
        public int Ordinal;            // element ordinal → probe key
    }

    // Control-flow signal for a per-line reject; caught by Run to accumulate.
    private sealed class LineReject : Exception
    {
        public LineReject(string code, string reason) { Code = code; Reason = reason; }
        public string Code { get; }
        public string Reason { get; }
    }

    // ──────────────────────────────────────────────────────────────── parse

    private sealed class Parser
    {
        private static readonly char[] Delims = { ' ', '+', '\t', '\n', '\r', '\f' };

        private readonly string _text;

        public readonly List<FalstadRejection> Rejections = new();
        public readonly List<FalstadNotice> Notices = new();
        private readonly List<Pending> _pending = new();
        private readonly List<PendingProbe> _probes = new();
        private readonly Dictionary<string, DiodeParams> _diodeModels = new(StringComparer.Ordinal);

        // Union-find over integer pixel coordinates.
        private readonly Dictionary<(int, int), (int, int)> _parent = new();
        private readonly HashSet<(int, int)> _groundCoords = new();
        private bool _needSyntheticReference;   // a rail (R) referenced ground but no g present

        public double? HeaderDt;

        public Parser(string text) => _text = text;

        // ---- run -------------------------------------------------------

        public void Run()
        {
            // First scan: diode model definitions (34) can be referenced by name from
            // d/z lines in any order, so collect them before parsing elements.
            var lines = _text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var toks = Tokenize(lines[i]);
                if (toks.Length == 0) continue;
                if (toks[0] == "34") TryDiodeModel(i + 1, lines[i], toks);
            }

            var ordinal = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var toks = Tokenize(raw);
                if (toks.Length == 0) continue;               // blank
                var code = toks[0];
                if (code.Length > 0 && code[0] == '#') continue;   // EA comment extension
                if (code == "34") continue;                   // handled in first scan

                try
                {
                    Dispatch(i + 1, raw, code, toks, ref ordinal);
                }
                catch (LineReject r)
                {
                    Rejections.Add(new FalstadRejection(i + 1, r.Code, r.Reason, raw));
                }
            }
        }

        private static string[] Tokenize(string line)
            => line.Split(Delims, StringSplitOptions.RemoveEmptyEntries);

        private void Dispatch(int line, string raw, string code, string[] t, ref int ordinal)
        {
            if (code == "$") { ReadHeader(t); return; }

            // Presentation-only, ignore-with-notice (falstad-format.md §7).
            switch (code)
            {
                case "o": Notice(line, code, "scope configuration ignored (display-only)."); return;
                case "h": Notice(line, code, "hint ignored (display-only)."); return;
                case "38": Notice(line, code, "adjustable slider ignored (UI-only)."); return;
                case "%": case "?": case "B": Notice(line, code, "afilter leftover ignored."); return;
                case "32": throw new LineReject(code, "transistor model definitions are unsupported.");
            }

            var first = code[0];
            if (first == '<')
                throw new LineReject(code, "XML <cir> dialect is unsupported — export as classic text.");

            // Digit-leading tokens are integer dump types; only the ones we handle
            // reach here — everything else is a loud rejection.
            if (char.IsDigit(first))
            {
                switch (code)
                {
                    case "172": throw new LineReject(code, "adjustable rail (172) is unsupported.");
                    default: throw new LineReject(code, $"dump type {code} is unsupported.");
                }
            }

            switch (code)
            {
                case "w": ReadWire(t); return;
                case "g": ReadGround(t); return;
                case "r": ReadResistor(t, ordinal++); return;
                case "v": ReadVoltageSource(t, ordinal++, onePost: false); return;
                case "R": ReadVoltageSource(t, ordinal++, onePost: true); return;
                case "i": ReadCurrentSource(t, ordinal++); return;
                case "c": ReadCapacitor(t, ordinal++); return;
                case "l": ReadInductor(t, ordinal++); return;
                case "s": ReadSwitch(t, ordinal++, line); return;
                case "d": ReadDiode(t, ordinal++, zener: false); return;
                case "z": ReadDiode(t, ordinal++, zener: true, line: line); return;
                case "O": ReadOutput(t, ordinal++); return;
                case "p": ReadVoltmeter(t, ordinal++, line); return;
                case "x": ordinal++; return;                 // text annotation: no electrical entity
                case "T": throw new LineReject(code, "transformer (T) import is not implemented (4-post winding geometry).");
                case "S": throw new LineReject(code, "SPDT switch (S) import is not implemented (throw-mirror geometry).");
                case "t": throw new LineReject(code, "transistor (t) is unsupported.");
                case "a": throw new LineReject(code, "op-amp (a) is unsupported.");
                case "I": case "L": case "M": throw new LineReject(code, $"logic element ({code}) is unsupported.");
                case "!": throw new LineReject(code, "custom logic model (!) is unsupported.");
                case ".": throw new LineReject(code, "subcircuit definition (.) is unsupported.");
                default: throw new LineReject(code, $"unrecognized element type {code}.");
            }
        }

        // ---- header ----------------------------------------------------

        private void ReadHeader(string[] t)
        {
            // $ flags maxTimeStep simSpeed currentSpeed voltageRange [powerBar [minStep]]
            if (t.Length < 6)
                throw new LineReject("$", $"header needs at least 5 fields, saw {t.Length - 1}.");
            var dt = ParseDouble(t[2], "$", "maxTimeStep");
            if (!(dt > 0) || double.IsInfinity(dt))
                throw new LineReject("$", $"header maxTimeStep must be finite and positive, saw {t[2]}.");
            HeaderDt = dt;
        }

        // ---- geometry / nodes -----------------------------------------

        private void Seed((int, int) c)
        {
            if (!_parent.ContainsKey(c)) _parent[c] = c;
        }

        private (int, int) Find((int, int) c)
        {
            var root = c;
            while (!_parent[root].Equals(root)) root = _parent[root];
            // Path compression.
            while (!_parent[c].Equals(root)) { var next = _parent[c]; _parent[c] = root; c = next; }
            return root;
        }

        private void Union((int, int) a, (int, int) b)
        {
            Seed(a); Seed(b);
            var ra = Find(a); var rb = Find(b);
            if (!ra.Equals(rb)) _parent[ra] = rb;
        }

        private void ReadWire(string[] t)
        {
            var a = Post(t, 0); var b = Post(t, 1);
            Union(a, b);
        }

        private void ReadGround(string[] t)
        {
            // 1-post: post1 is the electrical node; post2 is only a stub direction.
            var p = Post(t, 0);
            Seed(p);
            _groundCoords.Add(p);
        }

        // ---- primitives ------------------------------------------------

        private void ReadResistor(string[] t, int ord)
        {
            var (a, b) = TwoPosts(t);
            var ohms = ParamDouble(t, 6, "r", "resistance");
            RequirePositive(ohms, "r", "resistance");
            Add(new Pending { Kind = PendingKind.Resistor, Ax = a.Item1, Ay = a.Item2, Bx = b.Item1, By = b.Item2, Value = ohms, Ordinal = ord });
        }

        private void ReadVoltageSource(string[] t, int ord, bool onePost)
        {
            // v x1 y1 x2 y2 flags waveform freq maxVoltage bias phase [duty]
            // Post 2 (x2,y2) is the + terminal (VoltageElm polarity).
            var wf = (int)ParamDouble(t, 6, "v", "waveform");
            var freq = ParamDoubleOr(t, 7, 0.0);
            var maxV = ParamDoubleOr(t, 8, 0.0);
            var bias = ParamDoubleOr(t, 9, 0.0);
            var phase = ParamDoubleOr(t, 10, 0.0);
            RequireFinite(freq, "v", "frequency");
            RequireFinite(maxV, "v", "maxVoltage");
            RequireFinite(bias, "v", "bias");
            RequireFinite(phase, "v", "phaseShift");

            // 2-terminal `v`: the + terminal is post 2 (x2,y2) per VoltageElm polarity.
            // 1-post rail `R`: the single electrical post is post 1 (x1,y1) — RailElm has
            // getPostCount()==1 and stamps its source at getPost(0)=point1; (x2,y2) is only
            // the stub/symbol direction and is NOT the node other elements connect to
            // (falstad-format.md §5; mirrors ReadGround's 1-post convention).
            var pos = onePost ? Post(t, 0) : Post(t, 1);
            var neg = onePost ? default : Post(t, 0);
            Seed(pos);
            if (!onePost) Seed(neg);
            else _needSyntheticReference = true;

            if (wf == WfDc)
            {
                Add(new Pending
                {
                    Kind = PendingKind.VSource,
                    Ax = pos.Item1, Ay = pos.Item2,
                    Bx = neg.Item1, By = neg.Item2, BIsGround = onePost,
                    Value = maxV + bias, Ordinal = ord,
                });
            }
            else if (wf == WfAc)
            {
                if (bias != 0.0)
                    throw new LineReject(onePost ? "R" : "v", "sine source with a nonzero DC bias is unrepresentable (SineDrive has no offset).");
                Add(new Pending
                {
                    Kind = PendingKind.Sine,
                    Ax = pos.Item1, Ay = pos.Item2,
                    Bx = neg.Item1, By = neg.Item2, BIsGround = onePost,
                    Sine = new SineDrive(maxV, freq, phase), Ordinal = ord,
                });
            }
            else
            {
                throw new LineReject(onePost ? "R" : "v", $"waveform {wf} is unsupported (only DC=0 and sine=1 are accepted).");
            }
        }

        private void ReadCurrentSource(string[] t, int ord)
        {
            // i x1 y1 x2 y2 flags currentValue [maxVoltage]
            // Positive currentValue drives from post1 → post2 through the source.
            var (a, b) = TwoPosts(t);
            var amps = ParamDouble(t, 6, "i", "currentValue");
            RequireFinite(amps, "i", "currentValue");
            if (t.Length > 7)
            {
                var compliance = ParseDouble(t[7], "i", "maxVoltage");
                if (compliance != 0.0)
                    throw new LineReject("i", "current-source voltage compliance is unsupported (nonzero maxVoltage).");
            }
            Add(new Pending { Kind = PendingKind.ISource, Ax = a.Item1, Ay = a.Item2, Bx = b.Item1, By = b.Item2, Value = amps, Ordinal = ord });
        }

        private void ReadCapacitor(string[] t, int ord)
        {
            // c x1 y1 x2 y2 flags capacitance voltdiff [initialVoltage] [seriesR if flags&4]
            var (a, b) = TwoPosts(t);
            var flags = ParseInt(t[5], "c", "flags");
            var farads = ParamDouble(t, 6, "c", "capacitance");
            RequirePositive(farads, "c", "capacitance");
            var voltdiff = ParamDoubleOr(t, 7, 0.0);
            var ic = ParamDoubleOr(t, 8, 0.0);
            if (voltdiff != 0.0 || ic != 0.0)
                throw new LineReject("c", "nonzero capacitor initial voltage is unsupported (AddCapacitor has no IC parameter).");
            if ((flags & 4) != 0)
                throw new LineReject("c", "capacitor series resistance (flags&4) is unsupported (needs an internal node).");
            Add(new Pending { Kind = PendingKind.Capacitor, Ax = a.Item1, Ay = a.Item2, Bx = b.Item1, By = b.Item2, Value = farads, Ordinal = ord });
        }

        private void ReadInductor(string[] t, int ord)
        {
            // l x1 y1 x2 y2 flags inductance current [initialCurrent] [saturationCurrent]
            var (a, b) = TwoPosts(t);
            var henries = ParamDouble(t, 6, "l", "inductance");
            RequirePositive(henries, "l", "inductance");
            var current = ParamDoubleOr(t, 7, 0.0);
            var ic = ParamDoubleOr(t, 8, 0.0);
            var sat = ParamDoubleOr(t, 9, 0.0);
            if (current != 0.0 || ic != 0.0)
                throw new LineReject("l", "nonzero inductor initial current is unsupported (AddInductor has no IC parameter).");
            if (sat != 0.0)
                throw new LineReject("l", "nonlinear (saturating) inductor is out of scope (nonzero saturationCurrent).");
            Add(new Pending { Kind = PendingKind.Inductor, Ax = a.Item1, Ay = a.Item2, Bx = b.Item1, By = b.Item2, Value = henries, Ordinal = ord });
        }

        private void ReadSwitch(string[] t, int ord, int line = 0)
        {
            // s x1 y1 x2 y2 flags position momentary [label]. position 0 = closed, 1 = open.
            var (a, b) = TwoPosts(t);
            var posTok = t.Length > 6 ? t[6] : "0";
            bool closed = posTok switch
            {
                "0" or "false" => true,
                "1" or "true" => false,
                _ => throw new LineReject("s", $"switch position must be 0/1/true/false, saw {posTok}."),
            };
            // A momentary switch (t[7] truthy) imports as a plain toggle — the model has no
            // spring-return — but that downgrade must be user-visible (falstad-format.md §7).
            if (t.Length > 7 && (t[7] == "true" || t[7] == "1"))
                Notice(line, "s", "momentary switch imported as a plain toggle.");
            Add(new Pending { Kind = PendingKind.Switch, Ax = a.Item1, Ay = a.Item2, Bx = b.Item1, By = b.Item2, Value = closed ? 1.0 : 0.0, Ordinal = ord });
        }

        private void ReadDiode(string[] t, int ord, bool zener, int line = 0)
        {
            // d x1 y1 x2 y2 flags [fwdrop | modelName]. Anode = post1, cathode = post2.
            var (a, b) = TwoPosts(t);
            var flags = ParseInt(t[5], zener ? "z" : "d", "flags");
            const int flagFwdrop = 1, flagModel = 2;
            DiodeParams dp;
            if ((flags & flagModel) != 0)
            {
                var name = t.Length > 6 ? Unescape(t[6]) : "default";
                if (name is "default" or "default-zener") dp = DiodeParams.Default;
                else if (_diodeModels.TryGetValue(name, out var m)) dp = m;
                else throw new LineReject(zener ? "z" : "d", $"diode references unknown model '{name}'.");
            }
            else if ((flags & flagFwdrop) != 0)
            {
                throw new LineReject(zener ? "z" : "d", "diode forward-drop form is unsupported (fwdrop→saturation-current conversion not implemented).");
            }
            else
            {
                dp = DiodeParams.Default;
            }
            if (zener) Notice(line, "z", "zener imported as a plain diode (breakdown not modeled).");
            Add(new Pending { Kind = PendingKind.Diode, Ax = a.Item1, Ay = a.Item2, Bx = b.Item1, By = b.Item2, Diode = dp, Ordinal = ord });
        }

        private void TryDiodeModel(int line, string raw, string[] t)
        {
            // 34 escapedName flags Is Rs N BV forwardCurrent
            try
            {
                if (t.Length < 6) throw new LineReject("34", $"diode model needs name+flags+Is+Rs+N, saw {t.Length - 1} fields.");
                var name = Unescape(t[1]);
                var isat = ParseDouble(t[3], "34", "Is");
                var rs = ParseDouble(t[4], "34", "Rs");
                var n = ParseDouble(t[5], "34", "N");
                RequirePositive(isat, "34", "Is");
                RequirePositive(n, "34", "N");
                if (rs < 0) throw new LineReject("34", "diode model Rs must be non-negative.");
                _diodeModels[name] = new DiodeParams(isat, n, rs);
            }
            catch (LineReject r)
            {
                Rejections.Add(new FalstadRejection(line, r.Code, r.Reason, raw));
            }
        }

        // ---- probes ----------------------------------------------------

        private void ReadOutput(string[] t, int ord)
        {
            // O x1 y1 x2 y2 flags [scale]. 1-post: post1 is the measured node.
            var p = Post(t, 0);
            Seed(p);
            _probes.Add(new PendingProbe { X = p.Item1, Y = p.Item2, Ordinal = ord });
        }

        private void ReadVoltmeter(string[] t, int ord, int line)
        {
            // p x1 y1 x2 y2 flags [meter scale resistance]. Differential voltmeter;
            // imported as a single-node voltage probe at its first post.
            var (a, _) = TwoPosts(t);
            Seed(a);
            Notice(line, "p", "2-terminal voltmeter imported as a single-node probe at its first post.");
            _probes.Add(new PendingProbe { X = a.Item1, Y = a.Item2, Ordinal = ord });
        }

        // ---- build -----------------------------------------------------

        public FalstadImportResult Build(NetlistOptions? options)
        {
            // Unify all ground posts into one reference class.
            (int, int)? refRoot = null;
            (int, int)? firstGround = null;
            foreach (var gc in _groundCoords)
            {
                if (firstGround is null) firstGround = gc;
                else Union(firstGround.Value, gc);
            }
            if (firstGround is { } fg) refRoot = Find(fg);

            // Collect nodes: one per union root. Canonical coord = lexicographically
            // smallest member (order-independent node identity).
            var canonical = new Dictionary<(int, int), (int, int)>();
            foreach (var c in _parent.Keys)
            {
                var root = Find(c);
                if (!canonical.TryGetValue(root, out var cur) || Less(c, cur))
                    canonical[root] = c;
            }

            var opts = options ?? DefaultOptions(HeaderDt);
            var net = new Netlist(opts);

            var coordToNode = new Dictionary<(int, int), NodeId>();
            var rootToNode = new Dictionary<(int, int), NodeId>();
            var probes = new List<FalstadProbe>();
            NodeId referenceNode = default;
            var hasReference = refRoot is not null;

            using (var e = net.Edit())
            {
                // Deterministic creation order: sort roots by canonical coordinate.
                var roots = new List<(int, int)>(canonical.Keys);
                roots.Sort((ra, rb) =>
                {
                    var ca = canonical[ra]; var cb = canonical[rb];
                    return ca.Item1 != cb.Item1 ? ca.Item1.CompareTo(cb.Item1) : ca.Item2.CompareTo(cb.Item2);
                });

                foreach (var root in roots)
                {
                    var canon = canonical[root];
                    var key = new ExternalKey(NodeKeyTag, Pack(canon.Item1, canon.Item2));
                    var isRef = refRoot is { } rr && root.Equals(rr);
                    var node = isRef ? e.AddReferenceNode(key) : e.AddNode(key);
                    rootToNode[root] = node;
                    if (isRef) referenceNode = node;
                }

                // If a rail referenced ground but no g existed, synthesize a reference.
                if (!hasReference && _needSyntheticReference)
                {
                    var key = new ExternalKey(NodeKeyTag, 0xFFFF_FFFF_FFFF_FFFFUL);
                    referenceNode = e.AddReferenceNode(key);
                    hasReference = true;
                }
                // No ground at all but there are nodes: designate the smallest-coordinate
                // node as datum so the solve is not singular (deterministic choice).
                else if (!hasReference && roots.Count > 0)
                {
                    referenceNode = rootToNode[roots[0]];
                    hasReference = true;
                    Notice(0, "g", "no ground element; smallest-coordinate node designated as reference.");
                }

                // Map every coordinate to its node.
                foreach (var c in _parent.Keys)
                    coordToNode[c] = rootToNode[Find(c)];

                NodeId NodeAt(int x, int y, bool ground)
                    => ground ? referenceNode : coordToNode[(x, y)];

                foreach (var p in _pending)
                {
                    var ck = new ExternalKey(CompKeyTag, (ulong)p.Ordinal);
                    var sk = StateKey.From(ck);
                    var a = NodeAt(p.Ax, p.Ay, ground: false);
                    var b = NodeAt(p.Bx, p.By, p.BIsGround);
                    switch (p.Kind)
                    {
                        case PendingKind.Resistor: e.AddResistor(a, b, p.Value, ck); break;
                        case PendingKind.VSource: e.AddVoltageSource(a, b, p.Value, ck); break;
                        case PendingKind.Sine: e.AddSineSource(a, b, p.Sine, ck, sk); break;
                        case PendingKind.ISource: e.AddCurrentSource(a, b, p.Value, ck); break;
                        case PendingKind.Capacitor: e.AddCapacitor(a, b, p.Value, ck, sk); break;
                        case PendingKind.Inductor: e.AddInductor(a, b, p.Value, ck, sk); break;
                        case PendingKind.Switch: e.AddSwitch(a, b, p.Value != 0.0, ck); break;
                        case PendingKind.Diode: e.AddDiode(a, b, p.Diode, ck); break;
                    }
                }

                foreach (var pp in _probes)
                {
                    var node = coordToNode[(pp.X, pp.Y)];
                    var key = new ExternalKey(ProbeKeyTag, (ulong)pp.Ordinal);
                    var probe = e.AddProbe(node, key);
                    probes.Add(new FalstadProbe(pp.X, pp.Y, node, probe, key));
                }
            }

            return new FalstadImportResult(net, referenceNode, hasReference, HeaderDt, coordToNode, probes, Notices);
        }

        private static NetlistOptions DefaultOptions(double? headerDt)
            => headerDt is { } dt && dt > 0 ? NetlistOptions.Tablet(dt) : NetlistOptions.Tablet();

        // ---- helpers ---------------------------------------------------

        private void Add(in Pending p) => _pending.Add(p);

        private void Notice(int line, string code, string reason)
            => Notices.Add(new FalstadNotice(line, code, reason));

        private static bool Less((int, int) a, (int, int) b)
            => a.Item1 != b.Item1 ? a.Item1 < b.Item1 : a.Item2 < b.Item2;

        private static ulong Pack(int x, int y)
            => unchecked(((ulong)(uint)x << 32) | (uint)y);

        private (int, int) Post(string[] t, int which)
        {
            // which 0 → (x1,y1) at tokens[1..2]; which 1 → (x2,y2) at tokens[3..4].
            var xi = 1 + which * 2;
            if (t.Length <= xi + 1)
                throw new LineReject(t[0], "element line is missing post coordinates.");
            var x = ParseInt(t[xi], t[0], "x");
            var y = ParseInt(t[xi + 1], t[0], "y");
            var c = (x, y);
            Seed(c);   // every electrical post is a node candidate
            return c;
        }

        private ((int, int) a, (int, int) b) TwoPosts(string[] t)
        {
            if (t.Length < 6)
                throw new LineReject(t[0], $"2-terminal element needs at least 5 geometry/flag fields, saw {t.Length - 1}.");
            return (Post(t, 0), Post(t, 1));
        }

        private static double ParamDouble(string[] t, int idx, string code, string what)
        {
            if (t.Length <= idx) throw new LineReject(code, $"missing {what}.");
            return ParseDouble(t[idx], code, what);
        }

        private static double ParamDoubleOr(string[] t, int idx, double fallback)
            => t.Length > idx ? ParseDouble(t[idx], "?", "param") : fallback;

        private static double ParseDouble(string tok, string code, string what)
        {
            if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new LineReject(code, $"cannot parse {what} '{tok}' as a number.");
            if (double.IsNaN(v)) throw new LineReject(code, $"{what} is NaN.");
            return v;
        }

        private static int ParseInt(string tok, string code, string what)
        {
            if (!int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                throw new LineReject(code, $"cannot parse {what} '{tok}' as an integer.");
            return v;
        }

        private static void RequirePositive(double v, string code, string what)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
                throw new LineReject(code, $"{what} must be finite and positive, saw {v.ToString(CultureInfo.InvariantCulture)}.");
        }

        private static void RequireFinite(double v, string code, string what)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new LineReject(code, $"{what} must be finite, saw {v.ToString(CultureInfo.InvariantCulture)}.");
        }

        // circuitjs CustomLogicModel.unescape (falstad-format.md §4).
        private static string Unescape(string s)
        {
            if (s == "\\0") return string.Empty;
            if (s.IndexOf('\\') < 0) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                var n = s[++i];
                sb.Append(n switch
                {
                    'n' => '\n', 's' => ' ', 'p' => '+', 'q' => '=', 'h' => '#',
                    'a' => '&', 'r' => '\r', '\\' => '\\', _ => n,
                });
            }
            return sb.ToString();
        }
    }
}
