using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

internal sealed partial class Checker
{
    private readonly List<FunctionSymbol> _allFns = [];

    // ------------------------------------------------------------ functions

    private void DeclareFunction(FunctionDeclaration d, Scope scope)
    {
        string name = d.Name.Last.Text;
        if (d.TemplateParameters.Count > 0)
        {
            Err(SemanticCodes.Unsupported, "Function templates are not supported yet; write one overload per type.", d.Name.Span);
            return;
        }

        if (d.Name.Parts.Count > 1)
        {
            Err(SemanticCodes.Unsupported, "Defining a function outside its struct or namespace is not supported yet.", d.Name.Span);
            return;
        }

        if (name.StartsWith("operator", StringComparison.Ordinal))
        {
            Err(SemanticCodes.Unsupported, "Operator overloads are not supported yet.", d.Name.Span);
            return;
        }

        CheckAttributes(d.Attributes, "shader", "numthreads", "earlydepthstencil");
        var sym = new FunctionSymbol(name, d.Name.Span, d);
        sym.ReturnType = ResolveType(d.ReturnType);
        d.ResolvedReturnType = sym.ReturnType;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in d.Parameters)
        {
            var pt = ResolveType(p.Type);
            pt = ApplyDims(pt, p.ArrayDimensions, false, p.Span);
            p.ResolvedType = pt;
            if (pt.IsVoid)
            {
                Err(SemanticCodes.BadVariable, "A parameter cannot have type void.", p.Span);
                pt = HlslType.Error;
            }

            if (pt is ResourceType or SamplerType or ArrayType { Element: ResourceType or SamplerType })
            {
                sym.HasResourceParams = true;
                if ((p.Modifiers & Modifiers.Out) != 0)
                {
                    Err(SemanticCodes.BadResource, "A resource parameter cannot be 'out' or 'inout'.", p.Span);
                }
            }

            if (p.Name is not null && !names.Add(p.Name.Text))
            {
                Err(SemanticCodes.Redefinition, "The parameter '" + p.Name.Text + "' is declared twice; rename one.", p.Name.Span);
            }

            sym.Params.Add(new ParamInfo { Name = p.Name?.Text ?? "", Type = pt, Mods = p.Modifiers, Default = p.DefaultValue, Decl = p });
            if (p.DefaultValue is not null)
            {
                CheckExpr(p.DefaultValue);
                CheckConvertible(p.DefaultValue, pt);
            }
        }

        var shader = FindAttribute(d.Attributes, "shader");
        if (shader is not null)
        {
            string? stage = StringAttributeArg(shader, 0);
            switch (stage)
            {
                case "vertex":
                    sym.EntryStage = ShaderStage.Vertex;
                    break;
                case "pixel" or "fragment":
                    sym.EntryStage = ShaderStage.Pixel;
                    break;
                case "compute":
                    sym.EntryStage = ShaderStage.Compute;
                    break;
                default:
                    Err(SemanticCodes.BadEntryPoint, "The shader stage " + (stage is null ? "must be a string" : "'" + stage + "' is not supported") + ": use [shader(\"vertex\")], [shader(\"pixel\")] or [shader(\"compute\")].", shader.Span);
                    break;
            }
        }

        // Group with earlier overloads.
        var existing = scope.LookupLocal(name);
        FunctionGroup group;
        if (existing is FunctionGroup g)
        {
            group = g;
        }
        else if (existing is null)
        {
            group = new FunctionGroup(name, d.Name.Span);
            scope.Add(group);
        }
        else
        {
            Err(SemanticCodes.Redefinition, "'" + name + "' is already declared in this scope as a " + Describe(existing) + "; rename the function.", d.Name.Span);
            return;
        }

        FunctionSymbol? same = null;
        foreach (var o in group.Overloads)
        {
            if (SameSignature(o, sym))
            {
                same = o;
                break;
            }
        }

        if (same is not null)
        {
            if (same.HasBody && sym.HasBody)
            {
                Err(SemanticCodes.Redefinition, "The function '" + Signature(sym) + "' is already defined at line " + Num(same.Span.Line) + "; remove one definition or change the parameter types.", d.Name.Span);
                return;
            }

            if (sym.HasBody)
            {
                same.Definition = sym;
            }
            else
            {
                return;
            }
        }
        else
        {
            group.Overloads.Add(sym);
        }

        _allFns.Add(sym);
        if (sym.HasBody)
        {
            Functions.Add(sym);
            CheckFunctionBody(sym, scope);
        }
    }

    private static bool SameSignature(FunctionSymbol a, FunctionSymbol b)
    {
        if (a.Params.Count != b.Params.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Params.Count; i++)
        {
            if (!a.Params[i].Type.Equals(b.Params[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    private void CheckFunctionBody(FunctionSymbol f, Scope outer)
    {
        var savedScope = _scope;
        var savedFn = _fn;
        int savedLoop = _loopDepth;
        int savedSwitch = _switchDepth;
        _loopDepth = 0;
        _switchDepth = 0;
        _fn = f;
        f.InProgress = true;
        _scope = new Scope(outer);
        try
        {
            for (int i = 0; i < f.Params.Count; i++)
            {
                var p = f.Params[i];
                if (p.Name.Length == 0)
                {
                    continue;
                }

                var vs = new VariableSymbol(p.Name, p.Decl!.Name!.Span, VarKind.Param, p.Type, p.Mods) { Parameter = p.Decl };
                Define(_scope, vs);
                p.Symbol = vs;
            }

            CheckStatementList(f.Decl.Body!.Statements);
            CheckAllPathsReturn(f);
        }
        finally
        {
            f.InProgress = false;
            f.Checked = true;
            _scope = savedScope;
            _fn = savedFn;
            _loopDepth = savedLoop;
            _switchDepth = savedSwitch;
        }
    }

    // ------------------------------------------------------------ statements

    private void CheckStatementList(IReadOnlyList<Statement> list)
    {
        foreach (var s in list)
        {
            CheckStatement(s);
        }
    }

    private void CheckStatement(Statement s)
    {
        if (!Enter(s.Span))
        {
            Leave();
            return;
        }

        _ct.ThrowIfCancellationRequested();
        try
        {
            switch (s)
            {
                case BlockStatement b:
                {
                    var saved = _scope;
                    _scope = new Scope(saved);
                    CheckStatementList(b.Statements);
                    _scope = saved;
                    break;
                }

                case ExpressionStatement e:
                    CheckExpr(e.Expression);
                    break;
                case DeclarationStatement d:
                    CheckLocalDeclaration(d.Declaration);
                    break;
                case IfStatement i:
                    CheckCondition(i.Condition);
                    CheckScoped(i.Then);
                    if (i.Else is not null)
                    {
                        CheckScoped(i.Else);
                    }

                    break;
                case ForStatement f:
                {
                    var saved = _scope;
                    _scope = new Scope(saved);
                    if (f.Initializer is not null)
                    {
                        CheckStatement(f.Initializer);
                    }

                    if (f.Condition is not null)
                    {
                        CheckCondition(f.Condition);
                    }

                    if (f.Step is not null)
                    {
                        CheckExpr(f.Step);
                    }

                    _loopDepth++;
                    CheckScoped(f.Body);
                    _loopDepth--;
                    _scope = saved;
                    break;
                }

                case WhileStatement w:
                    CheckCondition(w.Condition);
                    _loopDepth++;
                    CheckScoped(w.Body);
                    _loopDepth--;
                    break;
                case DoWhileStatement dw:
                    _loopDepth++;
                    CheckScoped(dw.Body);
                    _loopDepth--;
                    CheckCondition(dw.Condition);
                    break;
                case SwitchStatement sw:
                    CheckSwitch(sw);
                    break;
                case BreakStatement br:
                    if (_loopDepth == 0 && _switchDepth == 0)
                    {
                        Err(SemanticCodes.BadJump, "'break' can only be used inside a loop or a switch.", br.Span);
                    }

                    break;
                case ContinueStatement co:
                    if (_loopDepth == 0)
                    {
                        Err(SemanticCodes.BadJump, "'continue' can only be used inside a loop.", co.Span);
                    }

                    break;
                case DiscardStatement:
                    if (_fn is not null)
                    {
                        _fn.Discards = true;
                    }

                    break;
                case ReturnStatement r:
                    CheckReturn(r);
                    break;
            }
        }
        finally
        {
            Leave();
        }
    }

    private void CheckScoped(Statement s)
    {
        var saved = _scope;
        _scope = new Scope(saved);
        CheckStatement(s);
        _scope = saved;
    }

    private void CheckCondition(Expression e)
    {
        var t = CheckExpr(e);
        if (t.IsError)
        {
            return;
        }

        if (t is not ScalarType)
        {
            Err(SemanticCodes.TypeMismatch, "A condition must be a single true/false or numeric value, not '" + t + "'. Use any(...) or all(...) to reduce a vector.", e.Span);
        }
    }

    private void CheckReturn(ReturnStatement r)
    {
        if (_fn is null)
        {
            return;
        }

        if (r.Value is null)
        {
            if (!_fn.ReturnType.IsVoid && !_fn.ReturnType.IsError)
            {
                Err(SemanticCodes.BadReturn, "The function '" + _fn.Name + "' returns '" + _fn.ReturnType + "'; give 'return' a value.", r.Span);
            }

            return;
        }

        if (r.Value is InitializerListExpression)
        {
            CheckInitializer(r.Value, _fn.ReturnType);
            return;
        }

        var t = CheckExpr(r.Value);
        if (_fn.ReturnType.IsVoid)
        {
            Err(SemanticCodes.BadReturn, "The function '" + _fn.Name + "' returns void; remove the value after 'return'.", r.Value.Span);
            return;
        }

        if (!t.IsError)
        {
            CheckConvertible(r.Value, _fn.ReturnType);
        }
    }

    private void CheckSwitch(SwitchStatement sw)
    {
        var st = CheckExpr(sw.Selector);
        if (!st.IsError && !(st is ScalarType { IsInteger: true }))
        {
            Err(SemanticCodes.TypeMismatch, "A switch selector must be an int or uint, not '" + st + "'.", sw.Selector.Span);
        }

        var seen = new HashSet<long>();
        bool hasDefault = false;
        _switchDepth++;
        var saved = _scope;
        _scope = new Scope(saved);
        foreach (var sec in sw.Sections)
        {
            foreach (var l in sec.Labels)
            {
                if (l.Value is null)
                {
                    if (hasDefault)
                    {
                        Err(SemanticCodes.Redefinition, "A switch can have only one 'default'.", l.Span);
                    }

                    hasDefault = true;
                    continue;
                }

                CheckExpr(l.Value);
                var c = l.Value.Info?.Const;
                if (c is null || c.Value.IsFloat)
                {
                    if (l.Value.Type is { IsError: false })
                    {
                        Err(SemanticCodes.NotConstant, "A case value must be an integer constant.", l.Value.Span);
                    }
                }
                else if (!seen.Add(c.Value.Int))
                {
                    Err(SemanticCodes.Redefinition, "The case value " + c.Value.Int.ToString(System.Globalization.CultureInfo.InvariantCulture) + " appears twice in this switch.", l.Value.Span);
                }
            }

            CheckStatementList(sec.Statements);
        }

        _scope = saved;
        _switchDepth--;
    }

    private void CheckLocalDeclaration(Declaration d)
    {
        switch (d)
        {
            case VariableDeclaration v:
                CheckLocalVariables(v);
                break;
            case TypedefDeclaration or StructDeclaration or EnumDeclaration:
                DeclareTopLevel(d, _scope);
                break;
            case PragmaDeclaration:
                break;
            case FunctionDeclaration f:
                Err(SemanticCodes.Unsupported, "Functions cannot be declared inside other functions.", f.Name.Span);
                break;
            default:
                Err(SemanticCodes.Unsupported, "This declaration is not allowed inside a function.", d.Span);
                break;
        }
    }

    private void CheckLocalVariables(VariableDeclaration v)
    {
        CheckAttributes(v.Attributes);
        var baseType = ResolveType(v.Type);
        var mods = v.Modifiers;
        foreach (var d in v.Declarators)
        {
            var type = ApplyDims(baseType, d.ArrayDimensions, d.Initializer is not null, d.Span);
            if (d.Initializer is InitializerListExpression il && type is ArrayType { Length: 0 } ua)
            {
                type = HlslType.Array(ua.Element, InferListLength(il, ua.Element));
            }

            d.Type = type;
            if (type.IsVoid)
            {
                Err(SemanticCodes.BadVariable, "A variable cannot have type void.", d.Name.Span);
                type = HlslType.Error;
            }
            else if (!type.IsError && !TypeOps.IsValueType(type))
            {
                Err(SemanticCodes.Unsupported, "A local variable of type '" + type + "' is not supported; resources must be declared at global scope.", d.Name.Span);
                type = HlslType.Error;
            }

            if ((mods & Modifiers.GroupShared) != 0)
            {
                Err(SemanticCodes.BadVariable, "'groupshared' variables must be declared at global scope.", d.Name.Span);
            }

            bool isConst = (mods & Modifiers.Const) != 0;
            if ((mods & Modifiers.Static) != 0 && !isConst)
            {
                Err(SemanticCodes.Unsupported, "Non-const 'static' local variables are not supported yet; move '" + d.Name.Text + "' to global scope.", d.Name.Span);
            }

            var sym = new VariableSymbol(d.Name.Text, d.Name.Span, VarKind.Local, type, mods) { Declarator = d, Initializer = d.Initializer };
            if (d.Initializer is not null)
            {
                CheckInitializer(d.Initializer, type);
                if (isConst && type is ScalarType st && d.Initializer.Info?.Const is { } cv)
                {
                    sym.Const = cv.ConvertTo(st);
                }
            }
            else if (isConst)
            {
                Err(SemanticCodes.BadVariable, "The constant '" + d.Name.Text + "' needs a value: write '= value' after the name.", d.Name.Span);
            }

            foreach (var a in d.Annotations)
            {
                if (a is not SemanticAnnotation)
                {
                    Err(SemanticCodes.BadVariable, "register and packoffset can only be used on global variables and constant buffer members.", a.Span);
                }
            }

            if (Define(_scope, sym))
            {
                d.Symbol = sym;
            }
        }
    }
}
