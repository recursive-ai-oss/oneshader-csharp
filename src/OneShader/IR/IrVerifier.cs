using System;
using System.Collections.Generic;
using OneShader.Types;

namespace OneShader.IR;

/// <summary>
/// Checks the structural rules every pass must preserve. It returns messages instead of throwing so
/// tests can show all problems at once. It checks block shape, operand ownership and ordering within a
/// block, types of the common opcodes and the structured-control-flow markers; it does not compute dominance.
/// </summary>
public static class IrVerifier
{
    /// <summary>Verifies a module; an empty list means it is well formed.</summary>
    public static IReadOnlyList<string> Verify(IrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in module.Functions)
        {
            if (!names.Add(f.Name))
            {
                errors.Add("duplicate function '" + f.Name + "'");
            }

            VerifyFunction(f, module, errors);
        }

        foreach (var e in module.EntryPoints)
        {
            if (!module.Functions.Contains(e.Function))
            {
                errors.Add("entry point '" + e.Function.Name + "' is not a function of the module");
            }

            if (e.Function.Parameters.Count != 0 || !e.Function.ReturnType.IsVoid)
            {
                errors.Add("entry point '" + e.Function.Name + "' must take no parameters and return void");
            }
        }

        return errors;
    }

    private static void VerifyFunction(IrFunction f, IrModule module, List<string> errors)
    {
        string where = "@" + f.Name;
        if (f.Blocks.Count == 0)
        {
            errors.Add(where + ": no blocks");
            return;
        }

        var blockSet = new HashSet<BasicBlock>(f.Blocks);
        var owned = new HashSet<Instruction>(ReferenceEqualityComparer.Instance);
        var globals = new HashSet<GlobalVariable>(module.Globals, ReferenceEqualityComparer.Instance);
        foreach (var b in f.Blocks)
        {
            foreach (var i in b.Instructions)
            {
                owned.Add(i);
            }
        }

        for (int bi = 0; bi < f.Blocks.Count; bi++)
        {
            var b = f.Blocks[bi];
            string bw = where + " bb" + bi;
            if (b.Instructions.Count == 0 || !b.Instructions[^1].IsTerminator)
            {
                errors.Add(bw + ": does not end in a terminator");
            }

            var seen = new HashSet<Instruction>(ReferenceEqualityComparer.Instance);
            bool phisDone = false;
            for (int ii = 0; ii < b.Instructions.Count; ii++)
            {
                var i = b.Instructions[ii];
                string iw = bw + " #" + ii + " (" + i.Op + ")";
                if (!ReferenceEquals(i.Parent, b))
                {
                    errors.Add(iw + ": Parent does not match the containing block");
                }

                if (i.IsTerminator && ii != b.Instructions.Count - 1)
                {
                    errors.Add(iw + ": terminator in the middle of a block");
                }

                if (i.Op == Opcode.Phi)
                {
                    if (phisDone)
                    {
                        errors.Add(iw + ": phi after a non-phi instruction");
                    }
                }
                else
                {
                    phisDone = true;
                }

                if (i.Op == Opcode.Alloca && bi != 0)
                {
                    errors.Add(iw + ": alloca outside the entry block");
                }

                foreach (var o in i.Operands)
                {
                    switch (o)
                    {
                        case null:
                            errors.Add(iw + ": null operand");
                            break;
                        case Instruction oi:
                            if (!owned.Contains(oi))
                            {
                                errors.Add(iw + ": operand is not an instruction of this function");
                            }
                            else if (ReferenceEquals(oi.Parent, b) && !seen.Contains(oi) && i.Op != Opcode.Phi)
                            {
                                errors.Add(iw + ": uses a value defined later in the same block");
                            }

                            if (oi.IsVoid)
                            {
                                errors.Add(iw + ": uses the result of a void instruction");
                            }

                            break;
                        case Argument a:
                            if (a.Index >= f.Parameters.Count || !ReferenceEquals(f.Parameters[a.Index], a))
                            {
                                errors.Add(iw + ": operand is not a parameter of this function");
                            }

                            break;
                        case GlobalVariable g:
                            if (!globals.Contains(g))
                            {
                                errors.Add(iw + ": global '" + g.Name + "' is not in the module");
                            }

                            break;
                    }
                }

                foreach (var t in i.Targets)
                {
                    if (!blockSet.Contains(t))
                    {
                        errors.Add(iw + ": target block is not in this function");
                    }
                }

                CheckTypes(i, iw, errors);
                seen.Add(i);
            }

            if (b.Structure != StructureKind.None)
            {
                if (b.MergeBlock is null || !blockSet.Contains(b.MergeBlock))
                {
                    errors.Add(bw + ": header without a merge block in this function");
                }

                var term = b.Terminator;
                if (b.Structure == StructureKind.Selection && term is not null && term.Op is not (Opcode.CondBranch or Opcode.Switch))
                {
                    errors.Add(bw + ": selection header must end in condbr or switch");
                }

                if (b.Structure == StructureKind.Loop)
                {
                    if (b.ContinueBlock is null || !blockSet.Contains(b.ContinueBlock))
                    {
                        errors.Add(bw + ": loop header without a continue block in this function");
                    }

                    if (term is not null && term.Op != Opcode.Branch)
                    {
                        errors.Add(bw + ": loop header must end in br");
                    }
                }
            }
        }

        if (errors.Count == 0)
        {
            CheckDominance(f, where, errors);
        }
    }

    // Every use must be dominated by its definition, and a phi must name exactly the block's predecessors.
    private static void CheckDominance(IrFunction f, string where, List<string> errors)
    {
        var cfg = new Cfg(f);
        for (int bi = 0; bi < f.Blocks.Count; bi++)
        {
            if (!cfg.IsReachable(bi))
            {
                continue;
            }

            var b = f.Blocks[bi];
            for (int ii = 0; ii < b.Instructions.Count; ii++)
            {
                var i = b.Instructions[ii];
                string iw = where + " bb" + bi + " #" + ii + " (" + i.Op + ")";
                if (i.Op == Opcode.Phi)
                {
                    var seenPreds = new HashSet<int>();
                    for (int k = 0; k < i.Targets.Count; k++)
                    {
                        int p = cfg.Index[i.Targets[k]];
                        if (!seenPreds.Add(p) || !cfg.Preds[bi].Contains(p))
                        {
                            errors.Add(iw + ": phi names a block that is not a (distinct) predecessor");
                            continue;
                        }

                        if (i.Operands[k] is Instruction d && cfg.IsReachable(p) && cfg.Index.TryGetValue(d.Parent!, out int db)
                            && (!cfg.IsReachable(db) || !cfg.Dominates(db, p)))
                        {
                            errors.Add(iw + ": phi value does not dominate the end of its predecessor");
                        }
                    }

                    if (seenPreds.Count != cfg.Preds[bi].Count)
                    {
                        errors.Add(iw + ": phi does not list every predecessor");
                    }

                    continue;
                }

                foreach (var o in i.Operands)
                {
                    if (o is Instruction d && !ReferenceEquals(d.Parent, b) && cfg.Index.TryGetValue(d.Parent!, out int db)
                        && (!cfg.IsReachable(db) || !cfg.Dominates(db, bi)))
                    {
                        errors.Add(iw + ": operand definition does not dominate the use");
                    }
                }
            }
        }
    }

    private static void CheckTypes(Instruction i, string w, List<string> errors)
    {
        int n = i.Operands.Count;
        switch (i.Op)
        {
            case Opcode.Load:
                if (n != 1 || i.Operands[0].Type is not PointerType lp || !lp.Pointee.Equals(i.Type))
                {
                    errors.Add(w + ": load needs one pointer operand whose pointee is the result type");
                }

                break;
            case Opcode.Store:
                if (n != 2 || i.Operands[0].Type is not PointerType sp || !sp.Pointee.Equals(i.Operands[1].Type))
                {
                    errors.Add(w + ": store needs a pointer and a value of the pointee type");
                }

                break;
            case Opcode.AccessChain:
                // The one chain without an index views a constant buffer's contents as its struct.
                bool contents = n == 1 && i.Operands[0].Type is PointerType { Pointee: ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer } };
                if ((n < 2 && !contents) || i.Operands[0].Type is not PointerType || i.Type is not PointerType)
                {
                    errors.Add(w + ": access needs a base pointer, at least one index and a pointer result");
                }

                break;
            case Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.Div or Opcode.Rem or Opcode.BitAnd or Opcode.BitOr
                or Opcode.BitXor or Opcode.Shl or Opcode.Shr or Opcode.LogicalAnd or Opcode.LogicalOr:
                if (n != 2 || !i.Operands[0].Type.Equals(i.Operands[1].Type) || !i.Type.Equals(i.Operands[0].Type))
                {
                    errors.Add(w + ": operands and result must have the same type");
                }

                break;
            case Opcode.Neg or Opcode.BitNot or Opcode.LogicalNot:
                if (n != 1 || !i.Type.Equals(i.Operands[0].Type))
                {
                    errors.Add(w + ": operand and result must have the same type");
                }

                break;
            case Opcode.Eq or Opcode.Ne or Opcode.Lt or Opcode.Le or Opcode.Gt or Opcode.Ge:
                if (n != 2 || !i.Operands[0].Type.Equals(i.Operands[1].Type) || i.Type.ScalarElement?.ScalarKind != ScalarKind.Bool)
                {
                    errors.Add(w + ": comparison needs two operands of one type and a bool result");
                }

                break;
            case Opcode.Select:
                if (n != 3 || !i.Operands[1].Type.Equals(i.Operands[2].Type) || !i.Type.Equals(i.Operands[1].Type)
                    || i.Operands[0].Type.ScalarElement?.ScalarKind != ScalarKind.Bool)
                {
                    errors.Add(w + ": select needs a bool condition and two values of the result type");
                }

                break;
            case Opcode.CondBranch:
                if (n != 1 || i.Operands[0].Type.Equals(ScalarType.Bool) is false || i.Targets.Count != 2)
                {
                    errors.Add(w + ": condbr needs one bool operand and two targets");
                }

                break;
            case Opcode.Branch:
                if (n != 0 || i.Targets.Count != 1)
                {
                    errors.Add(w + ": br needs exactly one target");
                }

                break;
            case Opcode.Switch:
                if (n != 1 || i.Targets.Count != i.Immediates.Count + 1)
                {
                    errors.Add(w + ": switch needs a selector, a default target and one target per case value");
                }

                break;
            case Opcode.Return:
                if (n > 1)
                {
                    errors.Add(w + ": ret takes at most one value");
                }

                break;
            case Opcode.Phi:
                if (n != i.Targets.Count || n == 0)
                {
                    errors.Add(w + ": phi needs one predecessor block per incoming value");
                }
                else
                {
                    foreach (var v in i.Operands)
                    {
                        if (!v.Type.Equals(i.Type))
                        {
                            errors.Add(w + ": phi incoming value has a different type");
                            break;
                        }
                    }
                }

                break;
            case Opcode.Call:
                if (i.Callee is null)
                {
                    errors.Add(w + ": call without a callee");
                }
                else if (n != i.Callee.Parameters.Count)
                {
                    errors.Add(w + ": call passes " + n + " arguments to a function with " + i.Callee.Parameters.Count + " parameters");
                }
                else
                {
                    for (int k = 0; k < n; k++)
                    {
                        if (!i.Operands[k].Type.Equals(i.Callee.Parameters[k].Type))
                        {
                            errors.Add(w + ": argument " + k + " has the wrong type");
                        }
                    }

                    if (!i.Type.Equals(i.Callee.ReturnType))
                    {
                        errors.Add(w + ": result type differs from the callee's return type");
                    }
                }

                break;
        }
    }

    /// <summary>Verifies a function that stands alone (no module): global operands are accepted as-is.</summary>
    public static IReadOnlyList<string> Verify(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var m = new IrModule();
        m.Functions.Add(function);
        var errors = new List<string>();
        var globals = new HashSet<GlobalVariable>(ReferenceEqualityComparer.Instance);
        foreach (var b in function.Blocks)
        {
            foreach (var i in b.Instructions)
            {
                foreach (var o in i.Operands)
                {
                    if (o is GlobalVariable g && globals.Add(g))
                    {
                        m.Globals.Add(g);
                    }
                }
            }
        }

        VerifyFunction(function, m, errors);
        return errors;
    }
}
