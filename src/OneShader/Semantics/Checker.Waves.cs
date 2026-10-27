using System;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Checking of the Shader Model 6 wave (subgroup) and quad intrinsics.</summary>
internal sealed partial class Checker
{
    private enum WaveForm
    {
        /// <summary>No arguments.</summary>
        Query,

        /// <summary>A bool in, a bool out (any/all true).</summary>
        VoteBool,

        /// <summary>A bool in, a uint out (count bits).</summary>
        BoolToUInt,

        /// <summary>A bool in, a uint4 out.</summary>
        Ballot,

        /// <summary>A 32-bit number in, the same type out.</summary>
        Numeric,

        /// <summary>A 32-bit integer in, the same type out.</summary>
        Integer,

        /// <summary>A 32-bit number in, bool of the same shape out.</summary>
        AllEqual,

        /// <summary>A 32-bit number and a lane index in, the number's type out.</summary>
        AtLane,
    }

    private static bool TryWaveEntry(string name, out Intrinsic ir, out WaveForm form)
    {
        form = WaveForm.Numeric;
        switch (name)
        {
            case "WaveGetLaneCount": ir = Intrinsic.WaveGetLaneCount; form = WaveForm.Query; return true;
            case "WaveGetLaneIndex": ir = Intrinsic.WaveGetLaneIndex; form = WaveForm.Query; return true;
            case "WaveIsFirstLane": ir = Intrinsic.WaveIsFirstLane; form = WaveForm.Query; return true;
            case "WaveActiveAnyTrue": ir = Intrinsic.WaveActiveAnyTrue; form = WaveForm.VoteBool; return true;
            case "WaveActiveAllTrue": ir = Intrinsic.WaveActiveAllTrue; form = WaveForm.VoteBool; return true;
            case "WaveActiveBallot": ir = Intrinsic.WaveActiveBallot; form = WaveForm.Ballot; return true;
            case "WaveActiveCountBits": ir = Intrinsic.WaveActiveCountBits; form = WaveForm.BoolToUInt; return true;
            case "WavePrefixCountBits": ir = Intrinsic.WavePrefixCountBits; form = WaveForm.BoolToUInt; return true;
            case "WaveActiveAllEqual": ir = Intrinsic.WaveActiveAllEqual; form = WaveForm.AllEqual; return true;
            case "WaveActiveSum": ir = Intrinsic.WaveActiveSum; return true;
            case "WaveActiveProduct": ir = Intrinsic.WaveActiveProduct; return true;
            case "WaveActiveMin": ir = Intrinsic.WaveActiveMin; return true;
            case "WaveActiveMax": ir = Intrinsic.WaveActiveMax; return true;
            case "WavePrefixSum": ir = Intrinsic.WavePrefixSum; return true;
            case "WavePrefixProduct": ir = Intrinsic.WavePrefixProduct; return true;
            case "WaveReadLaneFirst": ir = Intrinsic.WaveReadLaneFirst; return true;
            case "QuadReadAcrossX": ir = Intrinsic.QuadReadAcrossX; return true;
            case "QuadReadAcrossY": ir = Intrinsic.QuadReadAcrossY; return true;
            case "QuadReadAcrossDiagonal": ir = Intrinsic.QuadReadAcrossDiagonal; return true;
            case "WaveActiveBitAnd": ir = Intrinsic.WaveActiveBitAnd; form = WaveForm.Integer; return true;
            case "WaveActiveBitOr": ir = Intrinsic.WaveActiveBitOr; form = WaveForm.Integer; return true;
            case "WaveActiveBitXor": ir = Intrinsic.WaveActiveBitXor; form = WaveForm.Integer; return true;
            case "WaveReadLaneAt": ir = Intrinsic.WaveReadLaneAt; form = WaveForm.AtLane; return true;
            case "QuadReadLaneAt": ir = Intrinsic.QuadReadLaneAt; form = WaveForm.AtLane; return true;
            default: ir = default; return false;
        }
    }

    /// <summary>Checks a call to a wave or quad intrinsic. Returns false for a name that is not one.</summary>
    private bool TryWaveBuiltin(CallExpression c, string name, HlslType[] at, ExprInfo info, out HlslType result)
    {
        result = HlslType.Error;
        if (!TryWaveEntry(name, out var ir, out var form))
        {
            if (name.StartsWith("WaveMatch", StringComparison.Ordinal) || name.StartsWith("WaveMultiPrefix", StringComparison.Ordinal))
            {
                // SM 6.5 partitioned operations: Vulkan needs a vendor extension and neither Metal nor WGSL has an equivalent.
                Err(SemanticCodes.Unsupported, "The intrinsic '" + name + "' is not supported: it needs partitioned subgroup operations, which no target offers portably (SPIR-V has only a vendor extension; MSL and WGSL have none). Build the groups with WaveActiveBallot and WaveReadLaneFirst in a loop instead.", c.Span);
                return true;
            }

            if (name.StartsWith("Wave", StringComparison.Ordinal) || name.StartsWith("Quad", StringComparison.Ordinal))
            {
                Err(SemanticCodes.Unsupported, "The intrinsic '" + name + "' is not supported yet.", c.Span);
                return true;
            }

            return false;
        }

        int arity = form switch { WaveForm.Query => 0, WaveForm.AtLane => 2, _ => 1 };
        if (!ArityOk(c, name, arity) || AnyError(at))
        {
            return true;
        }

        HlslType[] ps;
        HlslType rt;
        switch (form)
        {
            case WaveForm.Query:
                ps = [];
                rt = ir == Intrinsic.WaveIsFirstLane ? ScalarType.Bool : ScalarType.UInt;
                break;
            case WaveForm.VoteBool or WaveForm.BoolToUInt or WaveForm.Ballot:
                if (at[0] is not ScalarType)
                {
                    Err(SemanticCodes.TypeMismatch, "'" + name + "' takes a single bool condition, not '" + at[0] + "'. Reduce a vector with any() or all().", c.Arguments[0].Span);
                    return true;
                }

                ps = [ScalarType.Bool];
                rt = form == WaveForm.VoteBool ? ScalarType.Bool : form == WaveForm.BoolToUInt ? ScalarType.UInt : HlslType.Vector(ScalarType.UInt, 4);
                break;
            default:
            {
                var t = at[0];
                var s = t.ScalarElement;
                bool shapeOk = t is ScalarType || t is VectorType;
                if (!shapeOk || s is null || !(s == ScalarType.Int || s == ScalarType.UInt || (s == ScalarType.Float && form != WaveForm.Integer)))
                {
                    Err(SemanticCodes.TypeMismatch, "'" + name + "' works on " + (form == WaveForm.Integer ? "int and uint" : "float, int and uint") + " scalars and vectors, not '" + t + "'. Convert the argument with a cast; 16- and 64-bit values, bools and matrices are not supported.", c.Arguments[0].Span);
                    return true;
                }

                ps = form == WaveForm.AtLane ? [t, ScalarType.UInt] : [t];
                rt = form == WaveForm.AllEqual ? TypeOps.Reshape(t, ScalarType.Bool) : t;
                break;
            }
        }

        info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = ir, Name = name, ParamTypes = ps, Result = rt };
        result = rt;
        return true;
    }
}
