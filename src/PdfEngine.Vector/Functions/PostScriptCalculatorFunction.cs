using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Functions;

/// <summary>
/// Type 4 PostScript calculator function (ISO 32000-2 7.10.5). The program is compiled once into flat
/// instruction blocks; evaluation runs on a bounded stack (<see cref="PdfSecurityLimits.MaxCalculatorStackDepth"/>)
/// with a bounded operator budget (<see cref="PdfSecurityLimits.MaxCalculatorOperations"/>).
/// </summary>
/// <remarks>
/// Runtime errors (stack underflow/overflow, type errors, division by zero, budget exhaustion) raise
/// <see cref="PdfUnsupportedFeatureException"/> with the reason supplied at parse time.
/// </remarks>
public sealed class PostScriptCalculatorFunction : PdfFunction
{
    private readonly Instruction[][] _blocks;
    private readonly int _maxStack;
    private readonly int _maxOperations;
    private readonly PdfFallbackReason _reason;

    private PostScriptCalculatorFunction(double[] domain, double[] range, Instruction[][] blocks, PdfSecurityLimits limits, PdfFallbackReason reason)
        : base(domain, range, range.Length / 2)
    {
        _blocks = blocks;
        _maxStack = Math.Clamp(limits.MaxCalculatorStackDepth, 1, 1000);
        _maxOperations = Math.Max(1, limits.MaxCalculatorOperations);
        _reason = reason;
    }

    internal static PostScriptCalculatorFunction Create(PdfStream stream, double[] domain, double[]? range, ParseContext ctx)
    {
        if (range == null)
            throw ctx.Fail("Type 4 function requires /Range.");

        byte[] code;
        try
        {
            code = new PdfStreamDecoder(ctx.Limits, ctx.Resolver.Resolve).DecodeStream(stream);
        }
        catch (PdfUnsupportedFeatureException ex)
        {
            throw ctx.Fail($"Type 4 function program cannot be decoded: {ex.Message}");
        }

        var blocks = Compile(code, ctx);
        return new PostScriptCalculatorFunction(domain, range, blocks, ctx.Limits, ctx.Reason);
    }

    /// <summary>Compiles and wraps a program directly (used by tests and callers holding program text).</summary>
    internal static PostScriptCalculatorFunction FromProgram(string program, double[] domain, double[] range, ParseContext ctx) =>
        new(domain, range, Compile(Encoding.ASCII.GetBytes(program), ctx), ctx.Limits, ctx.Reason);

    // ---------------------------------------------------------------- compilation

    private enum Op : byte
    {
        Push, Proc, If, IfElse,
        Abs, Add, Atan, Ceiling, Cos, Cvi, Cvr, Div, Exp, Floor, Idiv, Ln, Log, Mod, Mul, Neg, Round,
        Sin, Sqrt, Sub, Truncate,
        And, Or, Not, Xor, Bitshift,
        Eq, Ne, Gt, Ge, Lt, Le,
        Copy, Dup, Exch, Index, Pop, Roll,
    }

    private readonly record struct Instruction(Op Op, Value Operand = default, int Block1 = -1, int Block2 = -1);

    private static readonly Dictionary<string, Op> Operators = new(StringComparer.Ordinal)
    {
        ["abs"] = Op.Abs, ["add"] = Op.Add, ["atan"] = Op.Atan, ["ceiling"] = Op.Ceiling, ["cos"] = Op.Cos,
        ["cvi"] = Op.Cvi, ["cvr"] = Op.Cvr, ["div"] = Op.Div, ["exp"] = Op.Exp, ["floor"] = Op.Floor,
        ["idiv"] = Op.Idiv, ["ln"] = Op.Ln, ["log"] = Op.Log, ["mod"] = Op.Mod, ["mul"] = Op.Mul,
        ["neg"] = Op.Neg, ["round"] = Op.Round, ["sin"] = Op.Sin, ["sqrt"] = Op.Sqrt, ["sub"] = Op.Sub,
        ["truncate"] = Op.Truncate, ["and"] = Op.And, ["or"] = Op.Or, ["not"] = Op.Not, ["xor"] = Op.Xor,
        ["bitshift"] = Op.Bitshift, ["eq"] = Op.Eq, ["ne"] = Op.Ne, ["gt"] = Op.Gt, ["ge"] = Op.Ge,
        ["lt"] = Op.Lt, ["le"] = Op.Le, ["copy"] = Op.Copy, ["dup"] = Op.Dup, ["exch"] = Op.Exch,
        ["index"] = Op.Index, ["pop"] = Op.Pop, ["roll"] = Op.Roll,
    };

    private static Instruction[][] Compile(byte[] code, ParseContext ctx)
    {
        var blocks = new List<Instruction[]>();
        int pos = 0;
        string? first = NextToken(code, ref pos);
        if (first != "{")
            throw ctx.Fail("Type 4 program must start with '{'.");

        int main = CompileBlock(code, ref pos, blocks, ctx, nesting: 1);
        if (main != 0)
            throw ctx.Fail("Type 4 program structure is invalid.");
        return blocks.ToArray();
    }

    /// <summary>Compiles tokens up to the matching '}' into a new block and returns its index.</summary>
    private static int CompileBlock(byte[] code, ref int pos, List<Instruction[]> blocks, ParseContext ctx, int nesting)
    {
        if (nesting > ctx.Limits.MaxNestingDepth)
            throw ctx.Fail("Type 4 program nests procedures too deeply.");

        int index = blocks.Count;
        blocks.Add(Array.Empty<Instruction>()); // Reserve the slot so the main program is block 0.
        var instructions = new List<Instruction>();

        while (true)
        {
            string? token = NextToken(code, ref pos);
            if (token == null)
                throw ctx.Fail("Type 4 program is missing a closing '}'.");

            if (token == "}")
                break;

            if (token == "{")
            {
                int child = CompileBlock(code, ref pos, blocks, ctx, nesting + 1);
                instructions.Add(new Instruction(Op.Proc, Block1: child));
                continue;
            }

            if (token == "if")
            {
                if (instructions.Count < 1 || instructions[^1].Op != Op.Proc)
                    throw ctx.Fail("Type 4 'if' must follow a procedure.");
                int proc = instructions[^1].Block1;
                instructions[^1] = new Instruction(Op.If, Block1: proc);
                continue;
            }

            if (token == "ifelse")
            {
                if (instructions.Count < 2 || instructions[^1].Op != Op.Proc || instructions[^2].Op != Op.Proc)
                    throw ctx.Fail("Type 4 'ifelse' must follow two procedures.");
                int p1 = instructions[^2].Block1;
                int p2 = instructions[^1].Block1;
                instructions.RemoveAt(instructions.Count - 1);
                instructions[^1] = new Instruction(Op.IfElse, Block1: p1, Block2: p2);
                continue;
            }

            if (token == "true" || token == "false")
            {
                instructions.Add(new Instruction(Op.Push, Value.Bool(token == "true")));
                continue;
            }

            if (Operators.TryGetValue(token, out var op))
            {
                instructions.Add(new Instruction(op));
                continue;
            }

            if (TryParseNumber(token, out var number))
            {
                instructions.Add(new Instruction(Op.Push, number));
                continue;
            }

            throw ctx.Fail($"Type 4 program contains unknown token '{Truncate(token)}'.");
        }

        foreach (var instruction in instructions)
        {
            if (instruction.Op == Op.Proc)
                throw ctx.Fail("Type 4 procedure is not consumed by 'if' or 'ifelse'.");
        }

        blocks[index] = instructions.ToArray();
        return index;
    }

    private static string Truncate(string token) => token.Length <= 32 ? token : token[..32];

    private static string? NextToken(byte[] code, ref int pos)
    {
        while (pos < code.Length)
        {
            byte b = code[pos];
            if (b == '%')
            {
                while (pos < code.Length && code[pos] != '\n' && code[pos] != '\r') pos++;
                continue;
            }
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0)
            {
                pos++;
                continue;
            }
            break;
        }

        if (pos >= code.Length)
            return null;

        if (code[pos] == '{' || code[pos] == '}')
            return ((char)code[pos++]).ToString();

        int start = pos;
        while (pos < code.Length && code[pos] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0
                   or (byte)'{' or (byte)'}' or (byte)'%'))
        {
            pos++;
            if (pos - start > 256)
                break;
        }
        return Encoding.ASCII.GetString(code, start, pos - start);
    }

    private static bool TryParseNumber(string token, out Value value)
    {
        if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i))
        {
            value = Value.Int(i);
            return true;
        }
        if (double.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
        {
            value = Value.Real(d);
            return true;
        }
        value = default;
        return false;
    }

    // ---------------------------------------------------------------- evaluation

    private enum Kind : byte { Real, Int, Bool }

    private readonly record struct Value(double Number, Kind Kind)
    {
        public static Value Real(double v) => new(v, Kind.Real);
        public static Value Int(long v) => new(v, Kind.Int);
        public static Value Bool(bool v) => new(v ? 1 : 0, Kind.Bool);
        public bool IsNumber => Kind != Kind.Bool;
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> x, Span<double> y)
    {
        Span<Value> stack = stackalloc Value[_maxStack];
        int sp = 0;
        int operations = 0;

        for (int i = 0; i < x.Length; i++)
            Push(stack, ref sp, Value.Real(x[i]));

        Execute(0, stack, ref sp, ref operations);

        if (sp < y.Length)
            throw Fail($"Type 4 program left {sp} values; {y.Length} outputs expected.");

        for (int j = 0; j < y.Length; j++)
        {
            var v = stack[sp - y.Length + j];
            if (!v.IsNumber)
                throw Fail("Type 4 program produced a boolean output.");
            y[j] = v.Number;
        }
    }

    private PdfUnsupportedFeatureException Fail(string message) => new(_reason, message);

    private void Push(Span<Value> stack, ref int sp, Value value)
    {
        if (sp >= stack.Length)
            throw Fail($"Type 4 operand stack exceeded {stack.Length} entries.");
        stack[sp++] = value;
    }

    private Value PopValue(Span<Value> stack, ref int sp)
    {
        if (sp <= 0)
            throw Fail("Type 4 operand stack underflow.");
        return stack[--sp];
    }

    private double PopNumber(Span<Value> stack, ref int sp)
    {
        var v = PopValue(stack, ref sp);
        if (!v.IsNumber)
            throw Fail("Type 4 operator expected a number.");
        return v.Number;
    }

    private long PopInt(Span<Value> stack, ref int sp)
    {
        var v = PopValue(stack, ref sp);
        if (v.Kind != Kind.Int)
            throw Fail("Type 4 operator expected an integer.");
        return (long)v.Number;
    }

    private bool PopBool(Span<Value> stack, ref int sp)
    {
        var v = PopValue(stack, ref sp);
        if (v.Kind != Kind.Bool)
            throw Fail("Type 4 operator expected a boolean.");
        return v.Number != 0;
    }

    /// <summary>Int result when both operands are ints and the result is a 32-bit integer; real otherwise.</summary>
    private static Value Arith(Value a, Value b, double result) =>
        a.Kind == Kind.Int && b.Kind == Kind.Int && result >= int.MinValue && result <= int.MaxValue
            ? Value.Int((long)result)
            : Value.Real(result);

    private void Execute(int blockIndex, Span<Value> stack, ref int sp, ref int operations)
    {
        foreach (var ins in _blocks[blockIndex])
        {
            if (++operations > _maxOperations)
                throw Fail($"Type 4 program exceeded {_maxOperations} operations.");

            switch (ins.Op)
            {
                case Op.Push:
                    Push(stack, ref sp, ins.Operand);
                    break;

                case Op.If:
                    if (PopBool(stack, ref sp))
                        Execute(ins.Block1, stack, ref sp, ref operations);
                    break;

                case Op.IfElse:
                    Execute(PopBool(stack, ref sp) ? ins.Block1 : ins.Block2, stack, ref sp, ref operations);
                    break;

                case Op.Abs:
                    {
                        var a = PopValue(stack, ref sp);
                        if (!a.IsNumber) throw Fail("abs expects a number.");
                        Push(stack, ref sp, Arith(a, a, Math.Abs(a.Number)));
                        break;
                    }

                case Op.Neg:
                    {
                        var a = PopValue(stack, ref sp);
                        if (!a.IsNumber) throw Fail("neg expects a number.");
                        Push(stack, ref sp, Arith(a, a, -a.Number));
                        break;
                    }

                case Op.Add or Op.Sub or Op.Mul:
                    {
                        var b = PopValue(stack, ref sp);
                        var a = PopValue(stack, ref sp);
                        if (!a.IsNumber || !b.IsNumber) throw Fail("Arithmetic operator expects numbers.");
                        double r = ins.Op switch
                        {
                            Op.Add => a.Number + b.Number,
                            Op.Sub => a.Number - b.Number,
                            _ => a.Number * b.Number,
                        };
                        Push(stack, ref sp, Arith(a, b, r));
                        break;
                    }

                case Op.Div:
                    {
                        double b = PopNumber(stack, ref sp);
                        double a = PopNumber(stack, ref sp);
                        if (b == 0) throw Fail("div by zero.");
                        Push(stack, ref sp, Value.Real(a / b));
                        break;
                    }

                case Op.Idiv or Op.Mod:
                    {
                        long b = PopInt(stack, ref sp);
                        long a = PopInt(stack, ref sp);
                        if (b == 0) throw Fail("idiv/mod by zero.");
                        Push(stack, ref sp, Value.Int(ins.Op == Op.Idiv ? a / b : a % b));
                        break;
                    }

                case Op.Atan:
                    {
                        // num den atan -> angle in degrees in [0, 360).
                        double den = PopNumber(stack, ref sp);
                        double num = PopNumber(stack, ref sp);
                        if (num == 0 && den == 0) throw Fail("atan undefined for 0 0.");
                        double deg = Math.Atan2(num, den) * 180.0 / Math.PI;
                        if (deg < 0) deg += 360.0;
                        Push(stack, ref sp, Value.Real(deg));
                        break;
                    }

                case Op.Sin:
                    Push(stack, ref sp, Value.Real(Math.Sin(PopNumber(stack, ref sp) * Math.PI / 180.0)));
                    break;

                case Op.Cos:
                    Push(stack, ref sp, Value.Real(Math.Cos(PopNumber(stack, ref sp) * Math.PI / 180.0)));
                    break;

                case Op.Ceiling or Op.Floor or Op.Round or Op.Truncate:
                    {
                        var a = PopValue(stack, ref sp);
                        if (!a.IsNumber) throw Fail("Rounding operator expects a number.");
                        if (a.Kind == Kind.Int)
                        {
                            Push(stack, ref sp, a);
                            break;
                        }
                        double r = ins.Op switch
                        {
                            Op.Ceiling => Math.Ceiling(a.Number),
                            Op.Floor => Math.Floor(a.Number),
                            Op.Round => Math.Floor(a.Number + 0.5), // PostScript rounds halves up.
                            _ => Math.Truncate(a.Number),
                        };
                        Push(stack, ref sp, Value.Real(r));
                        break;
                    }

                case Op.Cvi:
                    {
                        double r = Math.Truncate(PopNumber(stack, ref sp));
                        if (r < int.MinValue || r > int.MaxValue) throw Fail("cvi result out of integer range.");
                        Push(stack, ref sp, Value.Int((long)r));
                        break;
                    }

                case Op.Cvr:
                    Push(stack, ref sp, Value.Real(PopNumber(stack, ref sp)));
                    break;

                case Op.Exp:
                    {
                        double exponent = PopNumber(stack, ref sp);
                        double @base = PopNumber(stack, ref sp);
                        double r = Math.Pow(@base, exponent);
                        if (!double.IsFinite(r)) throw Fail("exp result is undefined.");
                        Push(stack, ref sp, Value.Real(r));
                        break;
                    }

                case Op.Ln or Op.Log:
                    {
                        double a = PopNumber(stack, ref sp);
                        if (a <= 0) throw Fail("ln/log of a non-positive number.");
                        Push(stack, ref sp, Value.Real(ins.Op == Op.Ln ? Math.Log(a) : Math.Log10(a)));
                        break;
                    }

                case Op.Sqrt:
                    {
                        double a = PopNumber(stack, ref sp);
                        if (a < 0) throw Fail("sqrt of a negative number.");
                        Push(stack, ref sp, Value.Real(Math.Sqrt(a)));
                        break;
                    }

                case Op.And or Op.Or or Op.Xor:
                    {
                        var b = PopValue(stack, ref sp);
                        var a = PopValue(stack, ref sp);
                        if (a.Kind == Kind.Bool && b.Kind == Kind.Bool)
                        {
                            bool x1 = a.Number != 0, x2 = b.Number != 0;
                            Push(stack, ref sp, Value.Bool(ins.Op switch { Op.And => x1 & x2, Op.Or => x1 | x2, _ => x1 ^ x2 }));
                        }
                        else if (a.Kind == Kind.Int && b.Kind == Kind.Int)
                        {
                            int i1 = (int)a.Number, i2 = (int)b.Number;
                            Push(stack, ref sp, Value.Int(ins.Op switch { Op.And => i1 & i2, Op.Or => i1 | i2, _ => i1 ^ i2 }));
                        }
                        else
                        {
                            throw Fail("and/or/xor expect two booleans or two integers.");
                        }
                        break;
                    }

                case Op.Not:
                    {
                        var a = PopValue(stack, ref sp);
                        if (a.Kind == Kind.Bool) Push(stack, ref sp, Value.Bool(a.Number == 0));
                        else if (a.Kind == Kind.Int) Push(stack, ref sp, Value.Int(~(int)a.Number));
                        else throw Fail("not expects a boolean or an integer.");
                        break;
                    }

                case Op.Bitshift:
                    {
                        long shift = PopInt(stack, ref sp);
                        int value = (int)PopInt(stack, ref sp);
                        int result = shift >= 32 || shift <= -32 ? 0
                            : shift >= 0 ? value << (int)shift
                            : (int)((uint)value >> (int)-shift);
                        Push(stack, ref sp, Value.Int(result));
                        break;
                    }

                case Op.Eq or Op.Ne:
                    {
                        var b = PopValue(stack, ref sp);
                        var a = PopValue(stack, ref sp);
                        if (a.IsNumber != b.IsNumber) throw Fail("eq/ne operands have incompatible types.");
                        bool equal = a.Number == b.Number;
                        Push(stack, ref sp, Value.Bool(ins.Op == Op.Eq ? equal : !equal));
                        break;
                    }

                case Op.Gt or Op.Ge or Op.Lt or Op.Le:
                    {
                        double b = PopNumber(stack, ref sp);
                        double a = PopNumber(stack, ref sp);
                        bool r = ins.Op switch { Op.Gt => a > b, Op.Ge => a >= b, Op.Lt => a < b, _ => a <= b };
                        Push(stack, ref sp, Value.Bool(r));
                        break;
                    }

                case Op.Dup:
                    {
                        var a = PopValue(stack, ref sp);
                        Push(stack, ref sp, a);
                        Push(stack, ref sp, a);
                        break;
                    }

                case Op.Exch:
                    {
                        var b = PopValue(stack, ref sp);
                        var a = PopValue(stack, ref sp);
                        Push(stack, ref sp, b);
                        Push(stack, ref sp, a);
                        break;
                    }

                case Op.Pop:
                    PopValue(stack, ref sp);
                    break;

                case Op.Copy:
                    {
                        long n = PopInt(stack, ref sp);
                        if (n < 0 || n > sp) throw Fail("copy count out of range.");
                        int start = sp - (int)n;
                        for (int k = 0; k < n; k++)
                            Push(stack, ref sp, stack[start + k]);
                        break;
                    }

                case Op.Index:
                    {
                        long n = PopInt(stack, ref sp);
                        if (n < 0 || n >= sp) throw Fail("index out of range.");
                        Push(stack, ref sp, stack[sp - 1 - (int)n]);
                        break;
                    }

                case Op.Roll:
                    {
                        long j = PopInt(stack, ref sp);
                        long n = PopInt(stack, ref sp);
                        if (n < 0 || n > sp) throw Fail("roll count out of range.");
                        if (n == 0) break;
                        int count = (int)n;
                        int shift = (int)(((j % count) + count) % count);
                        if (shift == 0) break;
                        var window = stack.Slice(sp - count, count);
                        // Rolling "up" by j moves the top j elements to the bottom of the window.
                        window.Reverse();
                        window[..shift].Reverse();
                        window[shift..].Reverse();
                        break;
                    }

                default:
                    throw Fail($"Type 4 instruction {ins.Op} is not executable.");
            }
        }
    }
}
