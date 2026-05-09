using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssetsManager.Services.Parsers
{
    public class LuaParser
    {
        public Task<string> DecompileAsync(byte[] data) => Task.Run(() => Decompile(data));

        public string Decompile(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            try
            {
                using var reader = new FileReader(data);
                var fn = reader.NextFunctionBlock();
                return fn == null ? "-- No function blocks found." : new Generator().Generate(fn);
            }
            catch (Exception ex)
            {
                return $"-- Error decompiling Lua bytecode: {ex.Message}\n\n{ex.StackTrace}";
            }
        }

        // ── Constants ────────────────────────────────────────────────────────

        public enum LuaType { Nil = 0, Bool = 1, Number = 3, String = 4 }

        public abstract class Constant { public abstract override string ToString(); }

        public class NilConstant    : Constant { public override string ToString() => "nil"; }
        public class BoolConstant   : Constant
        {
            readonly bool _v; public BoolConstant(bool v) { _v = v; }
            public override string ToString() => _v ? "true" : "false";
        }
        public class NumberConstant : Constant
        {
            readonly double _v; public NumberConstant(double v) { _v = v; }
            public override string ToString()
                => (_v == Math.Floor(_v) && !double.IsInfinity(_v) && Math.Abs(_v) < 1e15)
                   ? ((long)_v).ToString() : _v.ToString("G");
        }
        public class StringConstant : Constant
        {
            readonly string _v; public StringConstant(string v) { _v = v; }
            public override string ToString()
            {
                if (string.IsNullOrEmpty(_v)) return "\"\"";
                string s = (_v.EndsWith("\0") ? _v[..^1] : _v)
                    .Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r");
                return '"' + s + '"';
            }
        }

        // ── Data structures ──────────────────────────────────────────────────

        public class Local
        {
            public string Name       { get; }
            public int    ScopeStart { get; }
            public int    ScopeEnd   { get; }
            public Local(string name, int start, int end) { Name = name; ScopeStart = start; ScopeEnd = end; }
        }

        public class Instruction
        {
            public enum Op
            {
                Move, LoadK, LoadBool, LoadNil, GetUpVal, GetGlobal, GetTable, SetGlobal, SetUpVal, SetTable,
                NewTable, Self, Add, Sub, Mul, Div, Mod, Pow, Unm, Not, Len, Concat, Jmp, Eq, Lt, Le,
                Test, TestSet, Call, TailCall, Return, ForLoop, ForPrep, TForLoop, SetList, Close, Closure, VarArg
            }

            private const int HalfMax18 = 131071;
            public Op  OpCode { get; }
            public int A      { get; }
            public int B      { get; }
            public int C      { get; }
            public int Bx     => ((B << 9) & 0xFFE00 | C) & 0x3FFFF;
            public int sBx    => Bx - HalfMax18;

            public Instruction(int data)
            {
                OpCode = (Op)(data & 0x3F);
                A = (data >> 6)  & 0xFF;
                B = (data >> 23) & 0x1FF;
                C = (data >> 14) & 0x1FF;
            }
        }

        public class Function
        {
            public enum VarArg { Has = 1, Is = 2, Needs = 4 }
            public string            SourceName;
            public int               LineNumber, LastLineNumber;
            public byte              NumUpvalues, NumParameters;
            public VarArg            VarArgFlag;
            public byte              MaxStackSize;
            public List<Instruction> Instructions;
            public List<Constant>    Constants;
            public List<Function>    Functions;
            public List<int>         SourceLinePositions;
            public List<Local>       Locals;
            public List<string>      Upvalues;
        }

        // ── FileReader ───────────────────────────────────────────────────────

        public class FileReader : IDisposable
        {
            private readonly MemoryStream _stream;
            private readonly BinaryReader _reader;
            private byte _intSize, _sizeTSize, _instrSize, _numSize;
            private bool _le;

            public FileReader(byte[] data)
            {
                _stream = new MemoryStream(data);
                _reader = new BinaryReader(_stream, Encoding.ASCII);
                ReadHeader();
            }

            public void Dispose() { _reader.Dispose(); _stream.Dispose(); }

            public Function NextFunctionBlock()
            {
                if (_stream.Position >= _stream.Length) return null;
                return new Function
                {
                    SourceName          = ReadString(),
                    LineNumber          = ReadInt(),
                    LastLineNumber      = ReadInt(),
                    NumUpvalues         = _reader.ReadByte(),
                    NumParameters       = _reader.ReadByte(),
                    VarArgFlag          = (Function.VarArg)_reader.ReadByte(),
                    MaxStackSize        = _reader.ReadByte(),
                    Instructions        = ReadList(_ => new Instruction(ReadInt(_instrSize))),
                    Constants           = ReadConstants(),
                    Functions           = ReadList(_ => NextFunctionBlock()),
                    SourceLinePositions = ReadList(_ => ReadInt() - 1),
                    Locals              = ReadList(_ => new Local(ReadString(), ReadInt(), ReadInt())),
                    Upvalues            = ReadList(_ => ReadString()),
                };
            }

            private void ReadHeader()
            {
                var b = _reader.ReadBytes(12);
                if (b.Length < 12) throw new InvalidDataException("File too short for a Lua header.");
                if (b[0] != 0x1b || b[1] != 'L' || b[2] != 'u' || b[3] != 'a')
                    throw new InvalidDataException("Invalid Lua bytecode signature.");
                if (b[4] != 0x51)
                    throw new NotSupportedException($"Only Lua 5.1 is supported (got 0x{b[4]:X2}).");
                _le = b[6] != 0;
                _intSize = b[7]; _sizeTSize = b[8]; _instrSize = b[9]; _numSize = b[10];
            }

            private List<T> ReadList<T>(Func<int, T> f)
            {
                int n = ReadInt(); var list = new List<T>(n);
                for (int i = 0; i < n; i++) list.Add(f(i));
                return list;
            }

            private List<Constant> ReadConstants()
            {
                int n = ReadInt(); var list = new List<Constant>(n);
                for (int i = 0; i < n; i++)
                {
                    byte tag = _reader.ReadByte();
                    list.Add((LuaType)tag switch
                    {
                        LuaType.Nil    => (Constant)new NilConstant(),
                        LuaType.Bool   => new BoolConstant(_reader.ReadBoolean()),
                        LuaType.Number => new NumberConstant(ReadNumber()),
                        LuaType.String => new StringConstant(ReadString()),
                        _              => throw new InvalidDataException($"Unknown constant type: {tag}"),
                    });
                }
                return list;
            }

            private string ReadString()
            {
                int sz = ReadInt(_sizeTSize);
                return sz == 0 ? string.Empty : Encoding.ASCII.GetString(_reader.ReadBytes(sz));
            }

            private int ReadInt(byte? size = null)
            {
                byte sz = size ?? _intSize;
                var b   = _reader.ReadBytes(sz);
                int r   = 0;
                if (_le) for (int i = 0; i < sz; i++) r |= b[i] << (i * 8);
                else     for (int i = 0; i < sz; i++) r  = (r << 8) | b[i];
                return r;
            }

            private double ReadNumber()
            {
                var b = _reader.ReadBytes(_numSize);
                return _numSize switch
                {
                    8 => BitConverter.ToDouble(b, 0),
                    4 => BitConverter.ToSingle(b,  0),
                    _ => throw new NotSupportedException($"Unsupported lua_Number size: {_numSize}"),
                };
            }
        }

        // ── Generator ───────────────────────────────────────────────────────

        public class Generator
        {
            private StringBuilder _sb;
            private int           _funcIdx;

            public string Generate(Function fn)
            {
                _sb = new StringBuilder(); _funcIdx = 0;
                EmitFunction(fn, isRoot: true, indent: 0);
                return _sb.ToString();
            }

            // ── Function ─────────────────────────────────────────────────────

            private void EmitFunction(Function fn, bool isRoot, int indent)
            {
                var localMap = BuildLocalMap(fn);

                if (!isRoot)
                {
                    _sb.Append(Tab(indent)).Append($"function func_{_funcIdx++}(");
                    EmitParams(fn, localMap);
                    _sb.AppendLine(")");
                }

                int childBase = _funcIdx;
                foreach (var child in fn.Functions)
                {
                    EmitFunction(child, isRoot: false, indent + (isRoot ? 0 : 1));
                    _sb.AppendLine();
                }

                EmitBody(fn, indent + (isRoot ? 0 : 1), localMap, childBase);

                if (!isRoot) _sb.Append(Tab(indent)).AppendLine("end");
            }

            private void EmitParams(Function fn, List<Local>[] localMap)
            {
                for (int p = 0; p < fn.NumParameters; p++)
                {
                    string name = (p < localMap.Length && localMap[p].Count > 0)
                        ? Clean(localMap[p][0].Name) ?? $"arg{p}" : $"arg{p}";
                    _sb.Append(name);
                    if (p + 1 < fn.NumParameters) _sb.Append(", ");
                }
                if ((fn.VarArgFlag & Function.VarArg.Has) != 0)
                {
                    if (fn.NumParameters > 0) _sb.Append(", ");
                    _sb.Append("...");
                }
            }

            // ── Body ─────────────────────────────────────────────────────────
            //
            // Lua 5.1 generic-for layout in bytecode:
            //   pc_open : JMP  → pc_tfor       (forward; emitted as "for x in y do")
            //   pc_open+1 .. pc_tfor-1 : body
            //   pc_tfor : TFORLOOP A C          (if results → jump back to body start; skipped)
            //   pc_back : JMP  → pc_tfor        (back-edge; skipped)
            //
            // Numeric-for layout:
            //   pc_prep : FORPREP → FORLOOP     (emitted as "for x = a, b, c do")
            //   body
            //   pc_loop : FORLOOP               (skipped; pendingEnds closes the block)

            private void EmitBody(Function fn, int baseIndent, List<Local>[] localMap, int childBase)
            {
                var regs  = new RegisterBank(fn, localMap);
                var instr = fn.Instructions;
                int count = instr.Count;

                // ── Pre-pass: identify structured loop boundaries ──────────────
                var numForEnd   = new Dictionary<int, int>(); // ForPrep pc → ForLoop pc
                var gForOpen    = new Dictionary<int, int>(); // opening JMP pc → TForLoop pc
                var gForSkipJmp = new HashSet<int>();          // back-edge JMP pcs (32-bit Lua only)

                for (int pc = 0; pc < count; pc++)
                {
                    var ins = instr[pc];

                    if (ins.OpCode == Instruction.Op.ForPrep)
                    {
                        int lpc = pc + 1 + ins.sBx;
                        if (lpc < count && instr[lpc].OpCode == Instruction.Op.ForLoop)
                            numForEnd[pc] = lpc;
                    }
                    else if (ins.OpCode == Instruction.Op.Jmp)
                    {
                        int target = pc + 1 + ins.sBx;
                        if (target >= 0 && target < count && instr[target].OpCode == Instruction.Op.TForLoop)
                        {
                            if (ins.sBx > 0) gForOpen[pc] = target; // forward → opening JMP
                            else             gForSkipJmp.Add(pc);    // backward → back-edge JMP (32-bit)
                        }
                    }
                }

                // All TForLoop pcs that are targets of an opening JMP — always skipped in emission
                var knownTForLoops = new HashSet<int>(gForOpen.Values);

                // PCs that are loop machinery — never emitted as ::label:: targets
                var structured = new HashSet<int>();
                foreach (var kv in numForEnd) { structured.Add(kv.Key); structured.Add(kv.Value); }
                foreach (var kv in gForOpen)
                {
                    structured.Add(kv.Key);     // opening JMP
                    structured.Add(kv.Value);   // TForLoop
                    structured.Add(kv.Key + 1); // body start — not a visible label
                }
                foreach (int pc in gForSkipJmp) structured.Add(pc);

                // Remaining forward/backward JMP targets need ::label:: markers
                var labels = new HashSet<int>();
                for (int pc = 0; pc < count; pc++)
                {
                    var ins = instr[pc];
                    if (ins.OpCode == Instruction.Op.Jmp && !structured.Contains(pc))
                        labels.Add(pc + 1 + ins.sBx);
                }

                // ── Emission pass ─────────────────────────────────────────────
                // pending: (closePc, depthToRestoreAfterClose)
                var pending = new Stack<(int closePc, int depth)>();
                int depth   = baseIndent;

                for (int pc = 0; pc < count; pc++)
                {
                    // Close any blocks whose end has arrived, restoring depth
                    while (pending.Count > 0 && pending.Peek().closePc == pc)
                    {
                        var (_, d) = pending.Pop();
                        depth = d;
                        _sb.AppendLine($"{Tab(depth)}end");
                    }

                    if (labels.Contains(pc))
                        _sb.AppendLine($"{Tab(depth)}::pc_{pc}::");

                    var    i = instr[pc];
                    string t = Tab(depth);

                    // ── Numeric for ───────────────────────────────────────────
                    if (i.OpCode == Instruction.Op.ForPrep && numForEnd.ContainsKey(pc))
                    {
                        int endPc = numForEnd[pc];
                        _sb.AppendLine($"{t}for {regs.NameAt(i.A + 3, pc + 1)} = "
                                     + $"{regs.Get(i.A)}, {regs.Get(i.A + 1)}, {regs.Get(i.A + 2)} do");
                        pending.Push((endPc + 1, depth));
                        depth++;
                        continue;
                    }
                    if (i.OpCode == Instruction.Op.ForLoop && numForEnd.ContainsValue(pc))
                        continue;

                    // ── Generic for ───────────────────────────────────────────
                    if (i.OpCode == Instruction.Op.Jmp && gForOpen.ContainsKey(pc))
                    {
                        int tforPc = gForOpen[pc];
                        var tfor   = instr[tforPc];
                        var vars   = Enumerable.Range(tfor.A + 3, tfor.C)
                                               .Select(x => regs.NameAt(x, pc + 1));
                        _sb.AppendLine($"{t}for {string.Join(", ", vars)} in {regs.Get(tfor.A)} do");
                        // TForLoop.sBx points back to the body start — use it to find block end.
                        // In 32-bit Lua a separate back-edge JMP exists after TForLoop;
                        // in 64-bit Lua TForLoop does the back-branch itself, so we close right after it.
                        int bodyStart = tforPc + 1 + tfor.sBx; // == pc_open + 1
                        int closePc   = gForSkipJmp.Count > 0   // 32-bit: back-edge JMP follows TForLoop
                            ? tforPc + 2 : tforPc + 1;
                        pending.Push((closePc, depth));
                        depth++;
                        continue;
                    }
                    // TForLoop is always loop machinery — skip unconditionally when known
                    if (i.OpCode == Instruction.Op.TForLoop && knownTForLoops.Contains(pc)) continue;
                    if (i.OpCode == Instruction.Op.Jmp      && gForSkipJmp.Contains(pc))    continue;

                    EmitInstruction(fn, i, ref pc, regs, t, childBase);
                }

                while (pending.Count > 0)
                {
                    var (_, d) = pending.Pop();
                    _sb.AppendLine($"{Tab(d)}end");
                }
            }

            // ── Instruction emission ──────────────────────────────────────────

            private void EmitInstruction(Function fn, Instruction i, ref int pc,
                                         RegisterBank regs, string t, int childBase)
            {
                switch (i.OpCode)
                {
                    // ── Loads / moves ─────────────────────────────────────────
                    case Instruction.Op.Move:     regs.Assign(i.A, regs.Get(i.B)); break;
                    case Instruction.Op.LoadK:    regs.Set(i.A, Const(i.Bx, fn));  break;
                    case Instruction.Op.LoadBool: regs.Set(i.A, i.B != 0 ? "true" : "false"); if (i.C != 0) pc++; break;
                    case Instruction.Op.LoadNil:  for (int x = i.A; x <= i.B; x++) regs.Set(x, "nil"); break;
                    case Instruction.Op.VarArg:
                    {
                        int n = i.B - 1;
                        if (n <= 0) { regs.Set(i.A, "..."); break; }
                        for (int x = 0; x < n; x++) regs.Set(i.A + x, "...");
                        break;
                    }

                    // ── Globals ───────────────────────────────────────────────
                    case Instruction.Op.GetGlobal: regs.Set(i.A, Const(i.Bx, fn).Trim('"')); break;
                    case Instruction.Op.SetGlobal: _sb.AppendLine($"{t}{Const(i.Bx, fn).Trim('"')} = {regs.Get(i.A)}"); break;

                    // ── Upvalues ──────────────────────────────────────────────
                    case Instruction.Op.GetUpVal:
                        regs.Set(i.A, i.B < fn.Upvalues.Count ? fn.Upvalues[i.B] : $"upval_{i.B}");
                        break;
                    case Instruction.Op.SetUpVal:
                        _sb.AppendLine($"{t}{(i.B < fn.Upvalues.Count ? fn.Upvalues[i.B] : $"upval_{i.B}")} = {regs.Get(i.A)}");
                        break;

                    // ── Tables ────────────────────────────────────────────────
                    case Instruction.Op.NewTable: regs.SetTable(i.A); break;

                    case Instruction.Op.GetTable:
                    {
                        string lhs = regs.NameAt(i.A, pc);
                        _sb.AppendLine($"{t}{lhs} = {TryDot(regs.Get(i.B), RK(i.C, fn, regs))}");
                        regs.Set(i.A, lhs);
                        break;
                    }

                    case Instruction.Op.SetTable:
                        if (regs.IsTable(i.A))
                            regs.AddTableEntry(i.A, RK(i.B, fn, regs), RK(i.C, fn, regs));
                        else
                            _sb.AppendLine($"{t}{TryDot(regs.Get(i.A), RK(i.B, fn, regs))} = {RK(i.C, fn, regs)}");
                        break;

                    case Instruction.Op.SetList:
                    {
                        int n = i.B == 0 ? fn.MaxStackSize - i.A - 1 : i.B;
                        int baseIdx = (i.C == 0 ? 1 : i.C) * 50;
                        for (int x = 1; x <= n; x++)
                        {
                            if (regs.IsTable(i.A)) regs.AddTableEntry(i.A, null, regs.Get(i.A + x));
                            else _sb.AppendLine($"{t}{regs.Get(i.A)}[{baseIdx - 50 + x}] = {regs.Get(i.A + x)}");
                        }
                        break;
                    }

                    // ── Arithmetic / unary ────────────────────────────────────
                    case Instruction.Op.Add:    regs.Set(i.A, $"{RK(i.B,fn,regs)} + {RK(i.C,fn,regs)}"); break;
                    case Instruction.Op.Sub:    regs.Set(i.A, $"{RK(i.B,fn,regs)} - {RK(i.C,fn,regs)}"); break;
                    case Instruction.Op.Mul:    regs.Set(i.A, $"{RK(i.B,fn,regs)} * {RK(i.C,fn,regs)}"); break;
                    case Instruction.Op.Div:    regs.Set(i.A, $"{RK(i.B,fn,regs)} / {RK(i.C,fn,regs)}"); break;
                    case Instruction.Op.Mod:    regs.Set(i.A, $"{RK(i.B,fn,regs)} % {RK(i.C,fn,regs)}"); break;
                    case Instruction.Op.Pow:    regs.Set(i.A, $"{RK(i.B,fn,regs)} ^ {RK(i.C,fn,regs)}"); break;
                    case Instruction.Op.Unm:    regs.Set(i.A, $"-{regs.Get(i.B)}");    break;
                    case Instruction.Op.Not:    regs.Set(i.A, $"not {regs.Get(i.B)}"); break;
                    case Instruction.Op.Len:    regs.Set(i.A, $"#{regs.Get(i.B)}");    break;
                    case Instruction.Op.Concat:
                        regs.Set(i.A, string.Join(" .. ", Enumerable.Range(i.B, i.C - i.B + 1).Select(x => regs.Get(x))));
                        break;

                    // ── Calls ─────────────────────────────────────────────────
                    case Instruction.Op.Self:
                    {
                        regs.Set(i.A + 1, regs.Get(i.B));
                        string k = RK(i.C, fn, regs);
                        regs.Set(i.A, $"{regs.Get(i.B)}:{(IsIdent(k, out string m) ? m : k.Trim('"'))}");
                        break;
                    }
                    case Instruction.Op.Call:     EmitCall(fn, i, regs, t, tail: false); break;
                    case Instruction.Op.TailCall: EmitCall(fn, i, regs, t, tail: true);  break;

                    case Instruction.Op.Return:
                        _sb.Append(t).Append("return");
                        if      (i.B > 1)  _sb.Append(" ").Append(string.Join(", ", Enumerable.Range(i.A, i.B - 1).Select(x => regs.Get(x))));
                        else if (i.B == 0) _sb.Append(" ...");
                        _sb.AppendLine();
                        break;

                    case Instruction.Op.Closure:
                        regs.Set(i.A, $"func_{childBase + i.Bx}");
                        break;

                    // ── Jumps ─────────────────────────────────────────────────
                    case Instruction.Op.Jmp:
                        _sb.AppendLine($"{t}goto pc_{pc + 1 + i.sBx}");
                        break;

                    // ── Comparisons (always followed by a Jmp) ────────────────
                    case Instruction.Op.Eq:
                    case Instruction.Op.Lt:
                    case Instruction.Op.Le:
                    {
                        string op   = i.OpCode == Instruction.Op.Eq ? "==" : i.OpCode == Instruction.Op.Lt ? "<" : "<=";
                        string cond = CompareCond(RK(i.B, fn, regs), RK(i.C, fn, regs), op, i.A);
                        pc++;
                        _sb.Append($"{t}if {cond} then "); EmitInlineJump(fn.Instructions[pc], pc); _sb.AppendLine(" end");
                        break;
                    }

                    case Instruction.Op.Test:
                    {
                        string cond = i.C == 0 ? regs.Get(i.A) : $"not {regs.Get(i.A)}";
                        pc++;
                        _sb.Append($"{t}if {cond} then "); EmitInlineJump(fn.Instructions[pc], pc); _sb.AppendLine(" end");
                        break;
                    }

                    case Instruction.Op.TestSet:
                    {
                        string cond = i.C == 0 ? regs.Get(i.B) : $"not {regs.Get(i.B)}";
                        string dest = regs.Name(i.A);
                        _sb.AppendLine($"{t}if {cond} then {dest} = {regs.Get(i.B)} end");
                        regs.Set(i.A, dest);
                        break;
                    }

                    case Instruction.Op.Close: break; // upvalue close — no output needed

                    default:
                        _sb.AppendLine($"{t}-- unhandled: {i.OpCode}");
                        break;
                }
            }

            // ── Helpers ───────────────────────────────────────────────────────

            private void EmitCall(Function fn, Instruction i, RegisterBank regs, string t, bool tail)
            {
                string args = i.B == 0 ? "..." : string.Join(", ", Enumerable.Range(i.A + 1, i.B - 1).Select(x => regs.Get(x)));
                string call = $"{regs.Get(i.A)}({args})";
                if (tail) { _sb.AppendLine($"{t}return {call}"); return; }
                int rc = i.C == 0 ? 1 : i.C - 1;
                if (rc <= 0) { _sb.AppendLine($"{t}{call}"); return; }
                var results = Enumerable.Range(i.A, rc).Select(x => { regs.Set(x, regs.Name(x)); return regs.Name(x); });
                _sb.AppendLine($"{t}{string.Join(", ", results)} = {call}");
            }

            private void EmitInlineJump(Instruction i, int pc)
            {
                if      (i.OpCode == Instruction.Op.Jmp)    _sb.Append($"goto pc_{pc + 1 + i.sBx}");
                else if (i.OpCode == Instruction.Op.Return)  _sb.Append("return");
                else                                          _sb.Append($"-- {i.OpCode}");
            }

            // A==0 → emit comparison directly; A==1 → negate (flip operator for readability)
            private static string CompareCond(string l, string r, string op, int a)
            {
                if (a == 0) return $"{l} {op} {r}";
                string neg = op switch { "==" => "~=", "<" => ">=", "<=" => ">", _ => null };
                return neg != null ? $"{l} {neg} {r}" : $"not ({l} {op} {r})";
            }

            // "tbl" + "\"field\"" → "tbl.field" when field is a valid identifier
            private static string TryDot(string tbl, string key)
                => IsIdent(key, out string f) ? $"{tbl}.{f}" : $"{tbl}[{key}]";

            private static bool IsIdent(string s, out string inner)
            {
                inner = null;
                if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                {
                    string c = s[1..^1];
                    if (!string.IsNullOrEmpty(c) && (char.IsLetter(c[0]) || c[0] == '_')
                        && c.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                    { inner = c; return true; }
                }
                return false;
            }

            private string RK(int val, Function fn, RegisterBank regs)
                => (val & 0x100) != 0 ? Const(val & 0xFF, fn) : regs.Get(val);

            private string Const(int idx, Function fn)
                => idx >= 0 && idx < fn.Constants.Count ? fn.Constants[idx].ToString() : $"const[{idx}]";

            private static string Tab(int n) => new string('\t', n);

            // Strip null-terminator; return null for Lua internal names like "(for index)"
            private static string Clean(string s)
            {
                if (string.IsNullOrEmpty(s)) return null;
                if (s.EndsWith("\0")) s = s[..^1];
                return s.StartsWith("(") ? null : s;
            }

            // ── Local-map builder ─────────────────────────────────────────────
            // Simulates Lua 5.1's register allocator: maps each slot to the list
            // of Local debug entries that ever occupied it (in declaration order).

            private static List<Local>[] BuildLocalMap(Function fn)
            {
                int size = fn.MaxStackSize > 0 ? fn.MaxStackSize : 256;
                var map  = Enumerable.Range(0, size).Select(_ => new List<Local>()).ToArray();
                var free = new int[size]; // free[r] = first pc where r is available again

                foreach (var loc in fn.Locals)
                {
                    int slot = Array.FindIndex(free, f => f <= loc.ScopeStart);
                    if (slot < 0) slot = fn.Locals.IndexOf(loc);
                    free[slot] = loc.ScopeEnd;
                    map[slot].Add(loc);
                }
                return map;
            }
        }

        // ── RegisterBank ─────────────────────────────────────────────────────

        private class RegisterBank
        {
            private readonly List<Local>[]                    _map;
            private readonly string[]                         _vals;
            private readonly bool[]                           _isTbl;
            private readonly List<(string Key, string Val)>[] _entries;

            public RegisterBank(Function fn, List<Local>[] map)
            {
                _map     = map;
                int sz   = map.Length;
                _vals    = new string[sz];
                _isTbl   = new bool[sz];
                _entries = new List<(string, string)>[sz];
                for (int r = 0; r < sz; r++) _vals[r] = DefaultName(r);
            }

            public string Name(int r)    => r < _map.Length ? DefaultName(r) : $"var{r}";
            public bool   IsTable(int r) => r < _map.Length && _isTbl[r];

            public string NameAt(int r, int pc)
            {
                if (r >= _map.Length) return $"var{r}";
                foreach (var loc in _map[r])
                    if (pc >= loc.ScopeStart && pc < loc.ScopeEnd)
                        return Sanitize(loc.Name) ?? DefaultName(r);
                return DefaultName(r);
            }

            public string Get(int r) => r < _map.Length ? Format(r) : $"var{r}";

            public void Set(int r, string v)    { if (r < _map.Length) { _vals[r] = v; _isTbl[r] = false; } }
            public void Assign(int r, string v) => Set(r, v);

            public void SetTable(int r)
            {
                if (r >= _map.Length) return;
                _isTbl[r]   = true;
                _entries[r] = new List<(string, string)>();
                _vals[r]    = null;
            }

            public void AddTableEntry(int r, string key, string val)
            {
                if (r < _map.Length && _isTbl[r]) _entries[r].Add((key, val));
            }

            // ── private ──────────────────────────────────────────────────────

            private string DefaultName(int r)
            {
                if (r < _map.Length && _map[r].Count > 0)
                    return Sanitize(_map[r][0].Name) ?? $"var{r}";
                return $"var{r}";
            }

            private string Format(int r)
            {
                if (!_isTbl[r]) return _vals[r] ?? DefaultName(r);
                var e = _entries[r];
                if (e == null || e.Count == 0) return "{}";
                if (e.All(x => x.Key == null) && e.Count < 8)
                    return "{ " + string.Join(", ", e.Select(x => x.Val)) + " }";
                var sb = new StringBuilder("{\n");
                foreach (var (k, v) in e)
                {
                    sb.Append("\t\t");
                    if (k != null) sb.Append(IsIdent(k, out string inner) ? inner : $"[{k}]").Append(" = ");
                    sb.AppendLine(v + ",");
                }
                return sb.Append("\t}").ToString();
            }

            private static string Sanitize(string s)
            {
                if (string.IsNullOrEmpty(s)) return null;
                if (s.EndsWith("\0")) s = s[..^1];
                return s.StartsWith("(") ? null : s;
            }

            internal static bool IsIdent(string s, out string inner)
            {
                inner = null;
                if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                {
                    string c = s[1..^1];
                    if (!string.IsNullOrEmpty(c) && (char.IsLetter(c[0]) || c[0] == '_')
                        && c.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                    { inner = c; return true; }
                }
                return false;
            }
        }
    }
}
