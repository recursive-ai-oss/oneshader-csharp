using System;
using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Msl;

/// <summary>
/// Cooperative matrices as arrangements of 8x8 <c>simdgroup_matrix</c> tiles (MSL 6.7): a 16x16 matrix is a struct holding a 2x2 grid of
/// tiles, one simdgroup owns the whole matrix, and each operation is written out per tile.
/// </summary>
internal sealed partial class MslEmitter
{
    private const int Tile = 8;

    private readonly Dictionary<CooperativeMatrixType, string> _coopNames = [];

    private static int TileRows(CooperativeMatrixType c) => c.Rows / Tile;

    private static int TileColumns(CooperativeMatrixType c) => c.Columns / Tile;

    /// <summary>Metal's simdgroup matrices exist for half, bfloat and float, in 8x8 tiles; anything else is refused up front with the line of its first use.</summary>
    private bool CheckCooperativeMatrices(WideTypeUse use)
    {
        foreach (var (type, span) in use.CooperativeTypes)
        {
            if (type.Rows % Tile != 0 || type.Columns % Tile != 0)
            {
                Unsupported("Metal's simdgroup_matrix works in 8x8 tiles, so a CooperativeMatrix needs rows and columns that are multiples of 8; this one is " + Inv(type.Rows) + " x " + Inv(type.Columns) + ". Use 16 x 16, or compile this shader for SPIR-V only.", span.IsKnown ? span : _ep.Function.Span);
            }
        }

        return !_failed;
    }

    /// <summary>The MSL struct for a cooperative matrix type, declared on first use.</summary>
    private string CooperativeName(CooperativeMatrixType c)
    {
        if (_coopNames.TryGetValue(c, out var name))
        {
            return name;
        }

        string elem = ScalarName(c.Element);
        string use = c.Use switch { CooperativeMatrixUse.A => "A", CooperativeMatrixUse.B => "B", _ => "Acc" };
        name = _names.ClaimExact("CoopMatrix_" + elem + "_" + Inv(c.Rows) + "x" + Inv(c.Columns) + "_" + use);
        _coopNames[c] = name;
        int tiles = Math.Max(1, TileRows(c) * TileColumns(c));
        _typeText.Append("struct ").Append(name).Append("\n{\n    simdgroup_matrix<").Append(elem).Append(", 8, 8> t[").Append(Inv(tiles)).Append("];\n};\n\n");
        return name;
    }

    private void DeclareValue(Instruction i)
    {
        if (!_hoisted.Contains(i))
        {
            W(T(i.Type) + " " + NameOf(i) + ";");
        }
    }

    private void EmitCooperative(Instruction i)
    {
        var o = i.Operands;
        switch (i.Intrinsic)
        {
            case Intrinsic.CoopMatrixLoad or Intrinsic.CoopMatrixStore:
                EmitCooperativeMemory(i);
                break;
            case Intrinsic.CoopMatrixMulAdd:
            {
                var a = (CooperativeMatrixType)o[0].Type;
                var c = (CooperativeMatrixType)i.Type;
                string n = NameOf(i);
                if (!_hoisted.Contains(i))
                {
                    W(T(i.Type) + " " + n + " = " + V(o[2]) + ";");
                }
                else
                {
                    W(n + " = " + V(o[2]) + ";");
                }

                string av = V(o[0]);
                string bv = V(o[1]);
                int kt = TileColumns(a);
                int nt = TileColumns(c);
                for (int r = 0; r < TileRows(c); r++)
                {
                    for (int col = 0; col < nt; col++)
                    {
                        string d = n + ".t[" + Inv(r * nt + col) + "]";
                        for (int k = 0; k < kt; k++)
                        {
                            W("simdgroup_multiply_accumulate(" + d + ", " + av + ".t[" + Inv(r * kt + k) + "], " + bv + ".t[" + Inv(k * nt + col) + "], " + d + ");");
                        }
                    }
                }

                break;
            }

            case Intrinsic.CoopMatrixLength:
            {
                // Each 8x8 tile gives a lane two elements (simdgroup_matrix::thread_elements).
                var c = (CooperativeMatrixType)o[0].Type;
                Define(i, Inv(TileRows(c) * TileColumns(c) * 2) + "u");
                break;
            }

            default:
            {
                var c = (CooperativeMatrixType)i.Type;
                string elem = ScalarName(c.Element);
                var tiles = new List<string>();
                for (int k = 0; k < TileRows(c) * TileColumns(c); k++)
                {
                    tiles.Add("make_filled_simdgroup_matrix<" + elem + ", 8, 8>(" + elem + "(0))");
                }

                Define(i, CooperativeName(c) + "{{" + string.Join(", ", tiles) + "}}");
                break;
            }
        }
    }

    /// <summary>
    /// Load and store, one <c>simdgroup_load</c> / <c>simdgroup_store</c> per tile. Row-major tile (r, c) starts at element
    /// (8r) * stride + 8c; column-major tile (r, c) starts at (8c) * stride + 8r and is transposed on the way (MSL 6.7.1).
    /// </summary>
    private void EmitCooperativeMemory(Instruction i)
    {
        var o = i.Operands;
        bool load = i.Intrinsic == Intrinsic.CoopMatrixLoad;
        var place = PlaceOf(o[0]);
        if (place.Kind is not (PlaceKind.Reg or PlaceKind.Mem))
        {
            Unsupported("A cooperative matrix reads and writes a structured buffer or a groupshared array.", i.Span);
            if (load)
            {
                Define(i, T(i.Type) + "()");
            }

            return;
        }

        var c = load ? (CooperativeMatrixType)i.Type : (CooperativeMatrixType)o[1].Type;
        bool column = i.Immediates[0] == 1;
        string ptr = Tmp("p");
        string stride = Tmp("s");
        W("auto " + ptr + " = &" + place.Expr + ";");
        W("ulong " + stride + " = " + V(o[load ? 1 : 2]) + ";");
        string matrix;
        if (load)
        {
            DeclareValue(i);
            matrix = NameOf(i);
        }
        else
        {
            matrix = V(o[1]);
        }

        int cols = TileColumns(c);
        for (int r = 0; r < TileRows(c); r++)
        {
            for (int col = 0; col < cols; col++)
            {
                string offset = column
                    ? Inv(Tile * col) + " * " + stride + " + " + Inv(Tile * r)
                    : Inv(Tile * r) + " * " + stride + " + " + Inv(Tile * col);
                string tile = matrix + ".t[" + Inv(r * cols + col) + "]";
                string tail = column ? ", ulong2(0, 0), true" : "";
                W((load ? "simdgroup_load(" : "simdgroup_store(") + tile + ", " + ptr + " + " + offset + ", " + stride + tail + ");");
            }
        }
    }
}
