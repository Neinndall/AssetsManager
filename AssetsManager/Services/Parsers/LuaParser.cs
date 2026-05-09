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
        /// <summary>
        /// Orchestrates the decompression/disassembly of Lua 5.1 bytecode files (.luabin64).
        /// </summary>
        public Task<string> DecompileAsync(byte[] data)
        {
            return Task.Run(() => Decompile(data));
        }

        public string Decompile(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;

            try
            {
                using (var reader = new FileReader(data))
                {
                    var function = reader.NextFunctionBlock();
                    if (function == null)
                        return "-- No function blocks found.";

                    var generator = new Generator();
                    return generator.Generate(function);
                }
            }
            catch (Exception ex)
            {
                return $"-- Error decompiling Lua bytecode: {ex.Message}\n\n{ex.StackTrace}";
            }
        }

        #region Internal Lua Decompiler Classes

        public enum LuaType
        {
            Nil = 0,
            Bool = 1,
            Number = 3,
            String = 4,
        }

        public abstract class Constant
        {
            public LuaType Type { get; protected set; }
            public override abstract string ToString();
        }

        public class Constant<T> : Constant
        {
            public T Value { get; private set; }
            protected Constant(LuaType type, T value)
            {
                Type = type;
                Value = value;
            }
            public override string ToString() => Value?.ToString() ?? "nil";
        }

        public class NilConstant : Constant<object>
        {
            public NilConstant() : base(LuaType.Nil, null) { }
            public override string ToString() => "nil";
        }

        public class BoolConstant : Constant<bool>
        {
            public BoolConstant(bool value) : base(LuaType.Bool, value) { }
            public override string ToString() => Value ? "true" : "false";
        }

        public class NumberConstant : Constant<double>
        {
            public NumberConstant(double value) : base(LuaType.Number, value) { }
        }

        public class StringConstant : Constant<string>
        {
            public StringConstant(string value) : base(LuaType.String, value) { }
            public override string ToString()
            {
                if (string.IsNullOrEmpty(Value)) return "\"\"";
                string val = Value.EndsWith("\0") ? Value.Substring(0, Value.Length - 1) : Value;
                return '\"' + val + '\"';
            }
        }

        public class Local
        {
            public string Name { get; private set; }
            public int ScopeStart { get; private set; }
            public int ScopeEnd { get; private set; }
            public Local(string name, int scopeStart, int scopeEnd)
            {
                Name = name;
                ScopeStart = scopeStart;
                ScopeEnd = scopeEnd;
            }
        }

        public class Instruction
        {
            public enum Op
            {
                Move, LoadK, LoadBool, LoadNil, GetUpVal, GetGlobal, GetTable, SetGlobal, SetUpVal, SetTable,
                NewTable, Self, Add, Sub, Mul, Div, Mod, Pow, Unm, Not, Len, Concat, Jmp, Eq, Lt, Le,
                Test, TestSet, Call, TailCall, Return, ForLoop, ForPrep, TForLoop, SetList, Close, Closure, VarArg
            }

            private const int HalfMax18Bit = 131071;
            public int Data { get; private set; }
            public Op OpCode { get; private set; }
            public int A { get; private set; }
            public int B { get; private set; }
            public int C { get; private set; }
            public int Bx => ((B << 9) & 0xFFE00 | C) & 0x3FFFF;
            public int sBx => Bx - HalfMax18Bit;
            public bool HasBx { get; private set; }
            public bool Signed { get; private set; }

            public Instruction(int data)
            {
                Data = data;
                OpCode = (Op)(data & 0x3F);
                A = (data >> 6) & 0xFF;
                B = (data >> 23) & 0x1FF;
                C = (data >> 14) & 0x1FF;

                switch (OpCode)
                {
                    case Op.Jmp:
                    case Op.ForLoop:
                    case Op.ForPrep:
                        Signed = true;
                        HasBx = true;
                        break;
                    case Op.LoadK:
                    case Op.GetGlobal:
                    case Op.SetGlobal:
                    case Op.Closure:
                        HasBx = true;
                        break;
                }
            }
        }

        public class Function
        {
            public enum VarArg { Has = 1, Is = 2, Needs = 4 }
            public string sourceName;
            public int lineNumber;
            public int lastLineNumber;
            public byte numUpvalues;
            public byte numParameters;
            public VarArg varArgFlag;
            public byte maxStackSize;
            public List<Instruction> instructions;
            public List<Constant> constants;
            public List<Function> functions;
            public List<int> sourceLinePositions;
            public List<Local> locals;
            public List<string> upvalues;
        }

        public struct FileHeader
        {
            public const int HeaderSize = 12;
            public const byte Lua51Version = 0x51;
            public string signature;
            public byte version;
            public byte format;
            public bool isLittleEndian;
            public byte intSize;
            public byte size_tSize;
            public byte instructionSize;
            public byte lua_NumberSize;
            public bool isIntegral;
        }

        public class FileReader : IDisposable
        {
            private MemoryStream memoryStream;
            private BinaryReader reader;
            private FileHeader header;

            public FileHeader Header => header;

            public FileReader(byte[] data)
            {
                memoryStream = new MemoryStream(data);
                reader = new BinaryReader(memoryStream, Encoding.ASCII);
                ReadHeader();
            }

            public Function NextFunctionBlock()
            {
                if (memoryStream.Position >= memoryStream.Length) return null;
                Function data = new Function();
                data.sourceName = ReadString();
                data.lineNumber = ReadInteger(header.intSize);
                data.lastLineNumber = ReadInteger(header.intSize);
                data.numUpvalues = reader.ReadByte();
                data.numParameters = reader.ReadByte();
                data.varArgFlag = (Function.VarArg)reader.ReadByte();
                data.maxStackSize = reader.ReadByte();
                data.instructions = ReadInstructions();
                data.constants = ReadConstants();
                data.functions = ReadFunctions();
                data.sourceLinePositions = ReadLineNumbers();
                data.locals = ReadLocals();
                data.upvalues = ReadUpvalues();
                return data;
            }

            public void Dispose()
            {
                reader.Dispose();
                memoryStream.Dispose();
            }

            private void ReadHeader()
            {
                byte[] bytes = reader.ReadBytes(12);
                if (bytes.Length < 12) throw new InvalidDataException("File is too short for a Lua header.");
                header.signature = new string(new char[] { (char)bytes[0], (char)bytes[1], (char)bytes[2], (char)bytes[3] });
                if (header.signature != (char)27 + "Lua") throw new InvalidDataException("Invalid Lua bytecode file.");
                header.version = bytes[4];
                if (header.version != FileHeader.Lua51Version) throw new NotImplementedException("Only Lua 5.1 is supported.");
                header.format = bytes[5];
                header.isLittleEndian = bytes[6] != 0;
                header.intSize = bytes[7];
                header.size_tSize = bytes[8];
                header.instructionSize = bytes[9];
                header.lua_NumberSize = bytes[10];
                header.isIntegral = bytes[11] != 0;
            }

            private List<Instruction> ReadInstructions()
            {
                int num = ReadInteger(header.intSize);
                var list = new List<Instruction>(num);
                for (int i = 0; i < num; i++) list.Add(new Instruction(ReadInteger(header.instructionSize)));
                return list;
            }

            private List<Constant> ReadConstants()
            {
                int num = ReadInteger(header.intSize);
                var list = new List<Constant>(num);
                for (int i = 0; i < num; i++)
                {
                    byte type = reader.ReadByte();
                    switch ((LuaType)type)
                    {
                        case LuaType.Nil: list.Add(new NilConstant()); break;
                        case LuaType.Bool: list.Add(new BoolConstant(reader.ReadBoolean())); break;
                        case LuaType.Number: list.Add(new NumberConstant(ReadNumber(header.lua_NumberSize))); break;
                        case LuaType.String: list.Add(new StringConstant(ReadString())); break;
                    }
                }
                return list;
            }

            private List<Function> ReadFunctions()
            {
                int num = ReadInteger(header.intSize);
                var list = new List<Function>(num);
                for (int i = 0; i < num; i++) list.Add(NextFunctionBlock());
                return list;
            }

            private List<int> ReadLineNumbers()
            {
                int num = ReadInteger(header.intSize);
                var list = new List<int>(num);
                for (int i = 0; i < num; i++) list.Add(ReadInteger(header.intSize) - 1);
                return list;
            }

            private List<Local> ReadLocals()
            {
                int num = ReadInteger(header.intSize);
                var list = new List<Local>(num);
                for (int i = 0; i < num; i++) list.Add(new Local(ReadString(), ReadInteger(header.intSize), ReadInteger(header.intSize)));
                return list;
            }

            private List<string> ReadUpvalues()
            {
                int num = ReadInteger(header.intSize);
                var list = new List<string>(num);
                for (int i = 0; i < num; i++) list.Add(ReadString());
                return list;
            }

            private string ReadString()
            {
                int size = ReadInteger(header.size_tSize);
                if (size == 0) return string.Empty;
                byte[] bytes = reader.ReadBytes(size);
                return Encoding.ASCII.GetString(bytes);
            }

            private int ReadInteger(byte size)
            {
                byte[] bytes = reader.ReadBytes(size);
                int ret = 0;
                if (header.isLittleEndian) { for (int i = 0; i < size; i++) ret += bytes[i] << (i * 8); }
                else { for (int i = 0; i < size; i++) ret = (ret << 8) | bytes[i]; }
                return ret;
            }

            private double ReadNumber(byte size)
            {
                byte[] bytes = reader.ReadBytes(size);
                if (size == 8) return BitConverter.ToDouble(bytes, 0);
                if (size == 4) return BitConverter.ToSingle(bytes, 0);
                throw new NotImplementedException("Unsupported lua_Number size: " + size);
            }
        }

        public class Generator
        {
            private StringBuilder _sb = new StringBuilder();
            private uint _functionCount;
            private RegisterState[] _regs;

            private class TableEntry
            {
                public string Key; // null for array part
                public string Value;
            }

            private class RegisterState
            {
                public string Value;
                public bool IsConstant;
                public List<TableEntry> TableEntries;
                public bool IsTable => TableEntries != null;

                public string ToLuaString(int indent = 0)
                {
                    if (!IsTable) return Value ?? "nil";
                    if (TableEntries.Count == 0) return "{}";

                    StringBuilder sb = new StringBuilder();
                    bool allArray = TableEntries.All(e => e.Key == null);

                    if (allArray && TableEntries.Count < 8)
                    {
                        sb.Append("{ ");
                        for (int i = 0; i < TableEntries.Count; i++)
                        {
                            sb.Append(TableEntries[i].Value);
                            if (i < TableEntries.Count - 1) sb.Append(", ");
                        }
                        sb.Append(" }");
                    }
                    else
                    {
                        sb.AppendLine("{");
                        string indents = new string('\t', indent + 1);
                        for (int i = 0; i < TableEntries.Count; i++)
                        {
                            sb.Append(indents);
                            if (TableEntries[i].Key != null)
                            {
                                string key = TableEntries[i].Key;
                                if (key.StartsWith("\"") && key.EndsWith("\""))
                                {
                                    string k = key.Trim('\"');
                                    if (IsValidIdentifier(k)) sb.Append(k);
                                    else sb.Append("[").Append(key).Append("]");
                                }
                                else sb.Append("[").Append(key).Append("]");
                                sb.Append(" = ");
                            }
                            sb.Append(TableEntries[i].Value);
                            if (i < TableEntries.Count - 1) sb.Append(",");
                            sb.AppendLine();
                        }
                        sb.Append(new string('\t', indent)).Append("}");
                    }
                    return sb.ToString();
                }

                private bool IsValidIdentifier(string s)
                {
                    if (string.IsNullOrEmpty(s)) return false;
                    if (!char.IsLetter(s[0]) && s[0] != '_') return false;
                    return s.All(c => char.IsLetterOrDigit(c) || c == '_');
                }
            }

            public string Generate(Function function)
            {
                _sb.Clear();
                _functionCount = 0;
                Write(function, 0);
                return _sb.ToString();
            }

            private void Write(Function function, int indentLevel)
            {
                if (function.lineNumber == 0 && function.lastLineNumber == 0)
                {
                    WriteChildFunctions(function, indentLevel);
                    WriteInstructions(function, indentLevel);
                }
                else
                {
                    string indents = new string('\t', indentLevel);
                    _sb.Append(indents).Append("function func").Append(_functionCount).Append("(");
                    for (int i = 0; i < function.numParameters; i++)
                        _sb.Append("arg").Append(i).Append(i + 1 != function.numParameters ? ", " : ")");
                    _sb.AppendLine();
                    _functionCount++;
                    WriteChildFunctions(function, indentLevel + 1);
                    WriteInstructions(function, indentLevel + 1);
                    _sb.Append(indents).AppendLine("end");
                }
            }

            private void WriteChildFunctions(Function function, int indentLevel)
            {
                foreach (var f in function.functions)
                {
                    Write(f, indentLevel);
                    _sb.AppendLine();
                }
            }

            private void WriteInstructions(Function function, int indentLevel)
            {
                string indents = new string('\t', indentLevel);
                _regs = new RegisterState[256];
                for (int i = 0; i < 256; i++) _regs[i] = new RegisterState { Value = "var" + i };

                // Pass 1: Identify jump targets
                HashSet<int> jumpTargets = new HashSet<int>();
                for (int pc = 0; pc < function.instructions.Count; pc++)
                {
                    var instr = function.instructions[pc];
                    if (instr.OpCode == Instruction.Op.Jmp || instr.OpCode == Instruction.Op.ForLoop || instr.OpCode == Instruction.Op.ForPrep)
                        jumpTargets.Add(pc + 1 + instr.sBx);
                }

                for (int pc = 0; pc < function.instructions.Count; pc++)
                {
                    if (jumpTargets.Contains(pc)) _sb.AppendFormat("{0}::pc_{1}::\n", indents, pc);

                    var i = function.instructions[pc];
                    switch (i.OpCode)
                    {
                        case Instruction.Op.Move:
                            _regs[i.A].Value = GetRegValue(i.B);
                            _regs[i.A].IsConstant = _regs[i.B].IsConstant;
                            _regs[i.A].TableEntries = _regs[i.B].TableEntries;
                            break;

                        case Instruction.Op.LoadK:
                            _regs[i.A].Value = GetConstant(i.Bx, function);
                            _regs[i.A].IsConstant = true;
                            _regs[i.A].TableEntries = null;
                            break;

                        case Instruction.Op.LoadBool:
                            _regs[i.A].Value = (i.B != 0 ? "true" : "false");
                            _regs[i.A].IsConstant = true;
                            _regs[i.A].TableEntries = null;
                            break;

                        case Instruction.Op.LoadNil:
                            for (int x = i.A; x <= i.B; x++) { _regs[x].Value = "nil"; _regs[x].IsConstant = true; _regs[x].TableEntries = null; }
                            break;

                        case Instruction.Op.GetGlobal:
                            _regs[i.A].Value = GetConstant(i.Bx, function).Trim('\"');
                            _regs[i.A].IsConstant = false;
                            _regs[i.A].TableEntries = null;
                            break;

                        case Instruction.Op.NewTable:
                            _regs[i.A].Value = null;
                            _regs[i.A].IsConstant = false;
                            _regs[i.A].TableEntries = new List<TableEntry>();
                            break;

                        case Instruction.Op.SetGlobal:
                            string globalName = GetConstant(i.Bx, function).Trim('\"');
                            _sb.AppendFormat("{2}{0} = {1}\n", globalName, _regs[i.A].ToLuaString(indentLevel), indents);
                            break;

                        case Instruction.Op.SetTable:
                            if (_regs[i.A].IsTable)
                                _regs[i.A].TableEntries.Add(new TableEntry { Key = WriteIndex(i.B, function), Value = WriteIndex(i.C, function) });
                            else
                                _sb.AppendFormat("{3}{0}[{1}] = {2}\n", GetRegValue(i.A), WriteIndex(i.B, function), WriteIndex(i.C, function), indents);
                            break;

                        case Instruction.Op.GetTable:
                            _sb.AppendFormat("{3}var{0} = {1}[{2}]\n", i.A, GetRegValue(i.B), WriteIndex(i.C, function), indents);
                            _regs[i.A].Value = "var" + i.A;
                            _regs[i.A].IsConstant = false;
                            _regs[i.A].TableEntries = null;
                            break;

                        case Instruction.Op.Add: _sb.AppendFormat("{3}var{0} = {1} + {2}\n", i.A, WriteIndex(i.B, function), WriteIndex(i.C, function), indents); _regs[i.A].TableEntries = null; break;
                        case Instruction.Op.Sub: _sb.AppendFormat("{3}var{0} = {1} - {2}\n", i.A, WriteIndex(i.B, function), WriteIndex(i.C, function), indents); _regs[i.A].TableEntries = null; break;
                        case Instruction.Op.Mul: _sb.AppendFormat("{3}var{0} = {1} * {2}\n", i.A, WriteIndex(i.B, function), WriteIndex(i.C, function), indents); _regs[i.A].TableEntries = null; break;
                        case Instruction.Op.Div: _sb.AppendFormat("{3}var{0} = {1} / {2}\n", i.A, WriteIndex(i.B, function), WriteIndex(i.C, function), indents); _regs[i.A].TableEntries = null; break;

                        case Instruction.Op.Call:
                            if (i.C != 0)
                            {
                                _sb.Append(indents);
                                int preLen = _sb.Length;
                                for (int x = i.A; x < i.A + i.C - 1; x++) _sb.AppendFormat("var{0}, ", x);
                                if (_sb.Length > preLen) { _sb.Remove(_sb.Length - 2, 2); _sb.Append(" = "); }
                            }
                            _sb.AppendFormat("{0}(", GetRegValue(i.A));
                            if (i.B != 0)
                            {
                                int preArgs = _sb.Length;
                                for (int x = i.A; x < i.A + i.B - 1; x++) _sb.AppendFormat("{0}, ", GetRegValue(x + 1));
                                if (_sb.Length > preArgs) _sb.Remove(_sb.Length - 2, 2);
                            }
                            else _sb.Append("...");
                            _sb.AppendLine(")");
                            break;

                        case Instruction.Op.Return:
                            _sb.Append(indents).Append("return");
                            if (i.B > 1)
                            {
                                _sb.Append(" ");
                                for (int x = i.A; x < i.A + i.B - 1; x++) _sb.AppendFormat("{0}, ", GetRegValue(x));
                                _sb.Remove(_sb.Length - 2, 2);
                            }
                            _sb.AppendLine();
                            break;

                        case Instruction.Op.Jmp: _sb.AppendFormat("{0}goto pc_{1}\n", indents, pc + 1 + i.sBx); break;
                        case Instruction.Op.Closure:
                            _regs[i.A].Value = "function_block_" + i.Bx;
                            _regs[i.A].IsConstant = false;
                            _regs[i.A].TableEntries = null;
                            break;

                        case Instruction.Op.SetList:
                            int n = i.B;
                            if (n == 0) n = (int)function.maxStackSize - i.A - 1;
                            if (_regs[i.A].IsTable)
                                for (int x = 1; x <= n; x++) _regs[i.A].TableEntries.Add(new TableEntry { Key = null, Value = GetRegValue(i.A + x) });
                            else
                                for (int x = 1; x <= n; x++) _sb.AppendFormat("{3}var{0}[{1}] = {2}\n", i.A, (i.C == 0 ? 0 : i.C - 1) * 50 + x, GetRegValue(i.A + x), indents);
                            break;

                        case Instruction.Op.GetUpVal:
                            _regs[i.A].Value = (i.B < function.upvalues.Count) ? function.upvalues[i.B] : "upval" + i.B;
                            _regs[i.A].IsConstant = false;
                            _regs[i.A].TableEntries = null;
                            break;

                        case Instruction.Op.SetUpVal:
                            string upName = (i.B < function.upvalues.Count) ? function.upvalues[i.B] : "upval" + i.B;
                            _sb.AppendFormat("{2}{0} = {1}\n", upName, _regs[i.A].ToLuaString(indentLevel), indents);
                            break;

                        case Instruction.Op.Self:
                            _regs[i.A + 1].Value = GetRegValue(i.B);
                            _regs[i.A + 1].TableEntries = _regs[i.B].TableEntries;
                            _regs[i.A].Value = GetRegValue(i.B) + ":" + WriteIndex(i.C, function).Trim('\"');
                            _regs[i.A].TableEntries = null;
                            break;

                        case Instruction.Op.Not: _regs[i.A].Value = "not " + GetRegValue(i.B); _regs[i.A].TableEntries = null; break;
                        case Instruction.Op.Len: _regs[i.A].Value = "#" + GetRegValue(i.B); _regs[i.A].TableEntries = null; break;
                        case Instruction.Op.Concat:
                            StringBuilder concatSb = new StringBuilder();
                            for (int x = i.B; x <= i.C; x++) { concatSb.Append(GetRegValue(x)); if (x < i.C) concatSb.Append(" .. "); }
                            _regs[i.A].Value = concatSb.ToString();
                            _regs[i.A].TableEntries = null;
                            break;

                        case Instruction.Op.Eq:
                        case Instruction.Op.Lt:
                        case Instruction.Op.Le:
                            _sb.AppendFormat("{3}if ({0} {4} {1}) ~= {2} then ", 
                                WriteIndex(i.B, function), 
                                WriteIndex(i.C, function), 
                                i.A, 
                                indents,
                                i.OpCode == Instruction.Op.Eq ? "==" : (i.OpCode == Instruction.Op.Lt ? "<" : "<="));
                            pc++;
                            WriteInlineInstruction(function.instructions[pc], pc, function);
                            _sb.Append(" end\n");
                            break;

                        case Instruction.Op.Test:
                            _sb.AppendFormat("{2}if not {0} <=> {1} then ", GetRegValue(i.A), i.C, indents);
                            pc++;
                            WriteInlineInstruction(function.instructions[pc], pc, function);
                            _sb.Append(" end\n");
                            break;

                        case Instruction.Op.TestSet:
                            _sb.AppendFormat("{3}if not {0} <=> {2} then var{1} = {0} end\n", GetRegValue(i.B), i.A, i.C, indents);
                            _regs[i.A].Value = "var" + i.A;
                            break;

                        case Instruction.Op.ForPrep: _sb.AppendFormat("{0}-- for prep\n", indents); break;
                        case Instruction.Op.ForLoop: _sb.AppendFormat("{0}-- for loop end\n", indents); break;
                        case Instruction.Op.TForLoop:
                            _sb.Append(indents);
                            for (int x = i.A + 3; x <= i.A + 2 + i.C; x++) _sb.AppendFormat("var{0}, ", x);
                            _sb.Remove(_sb.Length - 2, 2);
                            _sb.AppendFormat(" = var{0}(var{1}, var{2})\n", i.A, i.A + 1, i.A + 2);
                            break;

                        case Instruction.Op.VarArg:
                            for (int x = i.A; x < i.A + i.B - 1; x++) _regs[x].Value = "...";
                            break;
                    }
                }
            }

            private void WriteInlineInstruction(Instruction i, int pc, Function function)
            {
                switch (i.OpCode)
                {
                    case Instruction.Op.Jmp: _sb.AppendFormat("goto pc_{0}", pc + 1 + i.sBx); break;
                    case Instruction.Op.Call:
                        _sb.AppendFormat("{0}(", GetRegValue(i.A));
                        if (i.B != 0)
                        {
                            for (int x = i.A; x < i.A + i.B - 1; x++) _sb.AppendFormat("{0}{1}", GetRegValue(x + 1), (x + 1 < i.A + i.B - 1 ? ", " : ""));
                        }
                        else _sb.Append("...");
                        _sb.Append(")");
                        break;
                    case Instruction.Op.Return:
                        _sb.Append("return");
                        if (i.B > 1)
                        {
                            _sb.Append(" ");
                            for (int x = i.A; x < i.A + i.B - 1; x++) _sb.AppendFormat("{0}{1}", GetRegValue(x), (x < i.A + i.B - 2 ? ", " : ""));
                        }
                        break;
                    default: _sb.AppendFormat("-- {0}", i.OpCode); break;
                }
            }

            private string GetRegValue(int reg) => _regs[reg].ToLuaString();
            private string GetConstant(int idx, Function function) => (idx >= 0 && idx < function.constants.Count) ? function.constants[idx].ToString() : "const[" + idx + "]";

            private string WriteIndex(int val, Function func)
            {
                if ((val & 1 << 8) != 0) return GetConstant(val & ~(1 << 8), func);
                return GetRegValue(val);
            }
        }

        #endregion
    }
}
