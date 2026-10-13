using MBBSEmu.CPU;
using MBBSEmu.Memory;
using MBBSEmu.Session;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MBBSEmu.Diagnostics
{
    /// <summary>
    ///     Diagnostic write-watch on USER[n].substt (and USER[n].state) for one module's memory core.
    ///
    ///     Every write that lands on any channel's state/substt is attributed (CS:IP, BP call chain,
    ///     execution context, host operation) and kept in a ring buffer. When a channel that was inside
    ///     the module at a "realm" substt (>= RealmMin) drops below it, the event + ring + a snapshot of
    ///     all channels is written to substt-trace.log.
    ///
    ///     Enable with environment variable MBBSEMU_SUBSTT_TRACE=&lt;module identifier&gt; (or "*").
    ///     Optional: MBBSEMU_SUBSTT_MAP=path to a "SS:OOOO NAME" symbol map (default: &lt;module dir&gt;/mmud.map)
    ///               MBBSEMU_SUBSTT_REALMMIN=hex (default 82)
    ///               MBBSEMU_SUBSTT_ALL=1 log every substt change, not just realm exits
    /// </summary>
    public sealed class SubsttTracer
    {
        private const int UserSize = 41;
        private const int StateOff = 6;
        private const int SubsttOff = 8;

        // ---- execution context (set by ExecutionUnit / host) ----
        [ThreadStatic] private static Stack<(ICpuRegisters regs, ushort channel, FarPtr entry)> _ctx;
        [ThreadStatic] private static string _hostOp;

        public static void PushContext(ICpuRegisters regs, ushort channel, FarPtr entry) =>
            (_ctx ??= new()).Push((regs, channel, entry));
        public static void PopContext() { if (_ctx is { Count: > 0 }) _ctx.Pop(); }
        public static string HostOp { get => _hostOp; set => _hostOp = value; }

        private static readonly List<SubsttTracer> _all = new();
        public static void NoteAll(string what) { lock (_all) foreach (var t in _all) t.Note(what); }

        // ---- per-module instance ----
        private readonly string _moduleId;
        private readonly AbstractMemoryCore _mem;
        private readonly ushort _seg, _base;
        private readonly int _slots;
        private readonly PointerDictionary<SessionBase> _channels;
        private readonly Func<ushort> _hostChannelNumber;
        private readonly FarPtr _usrnum, _usrptr;
        private readonly ushort[] _shadowSubstt, _shadowState;
        private readonly bool[] _shadowValid;
        private readonly LinkedList<string> _ring = new();
        private readonly List<(ushort seg, ushort off, string name)> _syms = new();
        private readonly ushort _realmMin;
        private readonly bool _logAll;
        private readonly string _logPath;
        private readonly object _lock = new();
        private bool _inCallback;

        public static SubsttTracer TryCreate(string moduleId, string modulePath, IMemoryCore memory, FarPtr userBase,
            int slots, PointerDictionary<SessionBase> channels, Func<ushort> hostChannelNumber, FarPtr usrnum, FarPtr usrptr)
        {
            var want = Environment.GetEnvironmentVariable("MBBSEMU_SUBSTT_TRACE");
            if (string.IsNullOrEmpty(want)) return null;
            if (want != "*" && !want.Split(',').Any(w => string.Equals(w.Trim(), moduleId, StringComparison.OrdinalIgnoreCase)))
                return null;
            if (memory is not AbstractMemoryCore amc) return null;
            return new SubsttTracer(moduleId, modulePath, amc, userBase, slots, channels, hostChannelNumber, usrnum, usrptr);
        }

        private SubsttTracer(string moduleId, string modulePath, AbstractMemoryCore mem, FarPtr userBase, int slots,
            PointerDictionary<SessionBase> channels, Func<ushort> hostChannelNumber, FarPtr usrnum, FarPtr usrptr)
        {
            _usrnum = usrnum; _usrptr = usrptr;
            _moduleId = moduleId; _mem = mem; _seg = userBase.Segment; _base = userBase.Offset; _slots = slots;
            _channels = channels; _hostChannelNumber = hostChannelNumber;
            _shadowSubstt = new ushort[slots]; _shadowState = new ushort[slots]; _shadowValid = new bool[slots];
            _realmMin = Convert.ToUInt16(Environment.GetEnvironmentVariable("MBBSEMU_SUBSTT_REALMMIN") ?? "82", 16);
            _logAll = Environment.GetEnvironmentVariable("MBBSEMU_SUBSTT_ALL") == "1";
            _logPath = Path.Combine(Directory.GetCurrentDirectory(), "substt-trace.log");

            var map = Environment.GetEnvironmentVariable("MBBSEMU_SUBSTT_MAP") ?? Path.Combine(modulePath ?? ".", "mmud.map");
            if (File.Exists(map))
                foreach (var l in File.ReadAllLines(map))
                {
                    var p = l.Split(' ', 2);
                    var so = p[0].Split(':');
                    if (p.Length == 2 && so.Length == 2)
                        _syms.Add((Convert.ToUInt16(so[0], 16), Convert.ToUInt16(so[1], 16), p[1].Trim()));
                }
            _syms.Sort((a, b) => a.seg != b.seg ? a.seg.CompareTo(b.seg) : a.off.CompareTo(b.off));

            lock (_all) _all.Add(this);
            mem.ObservedSegment = _seg;
            mem.WriteObserver = OnWrite;
            Write($"==== {DateTime.Now:O} tracer armed module={moduleId} USER={_seg:X4}:{_base:X4} slots={slots} realmMin={_realmMin:X} syms={_syms.Count}");
        }

        /// <summary>Called by the host when a channel is added/removed so ghost lifetimes are visible.</summary>
        public void Note(string what)
        {
            lock (_lock)
            {
                if (what.StartsWith("CURUSR-REPARSE"))
                {
                    // Only interesting when the caller had staged INPUT itself (MajorMUD's queued-command executor)
                    var at = Where();
                    what += " at=" + at;
                    Add($"{DateTime.Now:HH:mm:ss.fff} NOTE {what}");
                    if (_logAll || _syms.Count == 0 || at.Contains("_EXECUTE_PENDING_COMMAND"))
                        Write($"{DateTime.Now:HH:mm:ss.fff} NOTE {what}");
                    return;
                }
                Add($"{DateTime.Now:HH:mm:ss.fff} NOTE {what}");
                if (_logAll) Write($"{DateTime.Now:HH:mm:ss.fff} NOTE {what}");
            }
        }

        /// <summary>curusr() refused a channel - always logged, this is the wrong-user hazard.</summary>
        public void CurusrBail(ushort requested)
        {
            lock (_lock)
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} CURUSR-BAIL req={requested} hostCh={Ch(_hostChannelNumber())} {HostInfo()} ctx={Ctx()} at={Where()}";
                Add(line);
                Write(line);
            }
        }

        private void OnWrite(ushort segment, ushort offset, int length)
        {
            if (_inCallback) return;
            int end = offset + length;
            int first = Math.Max(0, (offset - _base) / UserSize);
            int last = Math.Min(_slots - 1, (end - 1 - _base) / UserSize);
            if (end <= _base || first > last) return;

            for (var k = first; k <= last; k++)
            {
                int so = _base + k * UserSize;
                bool hitSub = offset < so + SubsttOff + 2 && end > so + SubsttOff;
                bool hitState = offset < so + StateOff + 2 && end > so + StateOff;
                if (!hitSub && !hitState) continue;

                _inCallback = true;
                try
                {
                    var nSub = _mem.GetWord(_seg, (ushort)(so + SubsttOff));
                    var nState = _mem.GetWord(_seg, (ushort)(so + StateOff));
                    if (!_shadowValid[k]) { _shadowValid[k] = true; _shadowSubstt[k] = nSub; _shadowState[k] = nState; continue; }
                    var oSub = _shadowSubstt[k]; var oState = _shadowState[k];
                    if (oSub == nSub && oState == nState) continue;
                    _shadowSubstt[k] = nSub; _shadowState[k] = nState;

                    lock (_lock)
                    {
                        var line = $"{DateTime.Now:HH:mm:ss.fff} slot={Ch(k)} state {oState}->{nState} substt {oSub:X}->{nSub:X} " +
                                   $"len={length} host={HostOp ?? "-"} hostCh={Ch(_hostChannelNumber())} {HostInfo()} ctx={Ctx()} at={Where()}";
                        Add(line);
                        bool realmExit = oSub >= _realmMin && oSub < 0x100 && nSub < _realmMin;
                        if (realmExit || _logAll)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine($"######## {(realmExit ? "REALM-EXIT" : "change")} {DateTime.Now:O}");
                            sb.AppendLine(line);
                            if (realmExit)
                            {
                                sb.AppendLine("-- snapshot (mem state/substt | session state/substt | user):");
                                for (var i = 0; i < _slots; i++)
                                {
                                    int s2 = _base + i * UserSize;
                                    var ms = _mem.GetWord(_seg, (ushort)(s2 + StateOff));
                                    var mu = _mem.GetWord(_seg, (ushort)(s2 + SubsttOff));
                                    if (ms == 0 && mu == 0 && !_channels.ContainsKey(i)) continue;
                                    var sess = _channels.ContainsKey(i) ? _channels[i] : null;
                                    sb.AppendLine($"   ch{i,3}: mem {ms,4}/{mu,4:X} | " +
                                                  (sess == null ? "NOT CONNECTED (ghost slot)" :
                                                   $"sess {sess.UsrPtr.State,4}/{sess.UsrPtr.Substt,4:X} {sess.Username} {sess.SessionState}"));
                                }
                                sb.AppendLine("-- recent history:");
                                foreach (var r in _ring) sb.AppendLine("   " + r);
                            }
                            Write(sb.ToString());
                        }
                    }
                }
                finally { _inCallback = false; }
            }
        }

        /// <summary>USRNUM and which USER slot *USRPTR currently points at.</summary>
        private string HostInfo()
        {
            try
            {
                var un = _usrnum.Segment == 0 ? -1 : _mem.GetWord(_usrnum.Segment, _usrnum.Offset);
                var po = _mem.GetWord(_usrptr.Segment, _usrptr.Offset);
                var ps = _mem.GetWord(_usrptr.Segment, (ushort)(_usrptr.Offset + 2));
                var slot = ps == _seg && po >= _base ? ((po - _base) / UserSize).ToString() : $"{ps:X4}:{po:X4}";
                return $"USRNUM={un} USRPTR->{slot}";
            }
            catch { return "USRNUM=?"; }
        }

        private string Ch(int k) => k == ushort.MaxValue ? "SYS" :
            k + (_channels.ContainsKey(k) ? $"({_channels[k].Username})" : "(ghost)");

        private static string Ctx()
        {
            if (_ctx is not { Count: > 0 }) return "none";
            return string.Join("<", _ctx.Select(c => $"[{(c.channel == ushort.MaxValue ? "SYS" : c.channel.ToString())} {c.entry.Segment:X}:{c.entry.Offset:X4}]"));
        }

        private string Where()
        {
            if (_ctx is not { Count: > 0 }) return "host";
            var r = _ctx.Peek().regs;
            var sb = new StringBuilder(Sym(r.CS, r.IP));
            try
            {
                ushort bp = r.BP;
                for (var i = 0; i < 10 && bp != 0 && bp < 0xFFF0; i++)
                {
                    var ip = _mem.GetWord(r.SS, (ushort)(bp + 2));
                    var cs = _mem.GetWord(r.SS, (ushort)(bp + 4));
                    if (cs == 0xFFFF || cs == 0) break;
                    sb.Append(" <- ").Append(Sym(cs, ip));
                    var nbp = _mem.GetWord(r.SS, bp);
                    if (nbp <= bp) break;
                    bp = nbp;
                }
            }
            catch { sb.Append(" <- ?"); }
            return sb.ToString();
        }

        private string Sym(ushort cs, ushort ip)
        {
            (ushort seg, ushort off, string name) best = default; var found = false;
            foreach (var s in _syms)
            {
                if (s.seg != cs) continue;
                if (s.off <= ip) { best = s; found = true; } else break;
            }
            return found ? $"{cs:X2}:{ip:X4}({best.name}+{ip - best.off:X})" : $"{cs:X2}:{ip:X4}";
        }

        private void Add(string s) { _ring.AddLast(s); while (_ring.Count > 400) _ring.RemoveFirst(); }

        private void Write(string s)
        {
            try { File.AppendAllText(_logPath, s + Environment.NewLine); } catch { /* never break the emulator */ }
        }
    }
}
