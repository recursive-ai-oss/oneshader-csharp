using System.Collections.Generic;

namespace OneShader.IR;

/// <summary>
/// Control-flow facts about one function at one moment: predecessors, reachability, reverse post-order,
/// immediate dominators (Cooper, Harvey and Kennedy's iterative algorithm) and dominance frontiers.
/// A pass rebuilds it after it changes the block graph; it never updates in place.
/// </summary>
internal sealed class Cfg
{
    private readonly int[] _in;
    private readonly int[] _out;
    private List<int>[]? _frontier;

    public Cfg(IrFunction function)
    {
        Function = function;
        Blocks = function.Blocks;
        int n = Blocks.Count;
        Index = new Dictionary<BasicBlock, int>(n, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++)
        {
            Index[Blocks[i]] = i;
        }

        Succs = new List<int>[n];
        Preds = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            Preds[i] = [];
        }

        for (int i = 0; i < n; i++)
        {
            var list = new List<int>();
            if (Blocks[i].Terminator is { } t)
            {
                foreach (var target in t.Targets)
                {
                    if (Index.TryGetValue(target, out int s) && !list.Contains(s))
                    {
                        list.Add(s);
                    }
                }
            }

            Succs[i] = list;
            foreach (int s in list)
            {
                Preds[s].Add(i);
            }
        }

        // Post-order by an explicit stack: shaders can nest deeply, and the compiler must not overflow.
        var order = new List<int>(n);
        var state = new byte[n];
        var stack = new Stack<(int Block, int Next)>();
        if (n > 0)
        {
            stack.Push((0, 0));
            state[0] = 1;
        }

        while (stack.Count > 0)
        {
            var (b, next) = stack.Pop();
            if (next < Succs[b].Count)
            {
                stack.Push((b, next + 1));
                int s = Succs[b][next];
                if (state[s] == 0)
                {
                    state[s] = 1;
                    stack.Push((s, 0));
                }
            }
            else
            {
                order.Add(b);
            }
        }

        order.Reverse();
        Rpo = order.ToArray();
        RpoNumber = new int[n];
        System.Array.Fill(RpoNumber, -1);
        for (int i = 0; i < Rpo.Length; i++)
        {
            RpoNumber[Rpo[i]] = i;
        }

        Idom = new int[n];
        System.Array.Fill(Idom, -1);
        if (n > 0)
        {
            Idom[0] = 0;
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int k = 1; k < Rpo.Length; k++)
                {
                    int b = Rpo[k];
                    int idom = -1;
                    foreach (int p in Preds[b])
                    {
                        if (RpoNumber[p] < 0 || Idom[p] < 0)
                        {
                            continue;
                        }

                        idom = idom < 0 ? p : Intersect(p, idom);
                    }

                    if (idom != Idom[b])
                    {
                        Idom[b] = idom;
                        changed = true;
                    }
                }
            }
        }

        DomChildren = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            DomChildren[i] = [];
        }

        for (int k = 1; k < Rpo.Length; k++)
        {
            int b = Rpo[k];
            if (Idom[b] >= 0)
            {
                DomChildren[Idom[b]].Add(b);
            }
        }

        // Pre/post numbers on the dominator tree make Dominates O(1).
        _in = new int[n];
        _out = new int[n];
        int clock = 0;
        var walk = new Stack<(int Block, int Next)>();
        if (n > 0)
        {
            walk.Push((0, 0));
            _in[0] = clock++;
        }

        while (walk.Count > 0)
        {
            var (b, next) = walk.Pop();
            if (next < DomChildren[b].Count)
            {
                walk.Push((b, next + 1));
                int c = DomChildren[b][next];
                _in[c] = clock++;
                walk.Push((c, 0));
            }
            else
            {
                _out[b] = clock++;
            }
        }
    }

    public IrFunction Function { get; }

    public List<BasicBlock> Blocks { get; }

    public Dictionary<BasicBlock, int> Index { get; }

    /// <summary>Distinct successors per block, in terminator order.</summary>
    public List<int>[] Succs { get; }

    /// <summary>Distinct predecessors per block, in block order. Includes predecessors that are themselves unreachable.</summary>
    public List<int>[] Preds { get; }

    /// <summary>Reachable blocks in reverse post-order; the entry is first.</summary>
    public int[] Rpo { get; }

    /// <summary>Position in <see cref="Rpo"/>, or -1 for an unreachable block.</summary>
    public int[] RpoNumber { get; }

    /// <summary>Immediate dominator; the entry is its own; -1 for unreachable blocks.</summary>
    public int[] Idom { get; }

    /// <summary>Dominator-tree children in reverse post-order.</summary>
    public List<int>[] DomChildren { get; }

    public bool IsReachable(int block) => RpoNumber[block] >= 0;

    /// <summary>True when <paramref name="a"/> dominates <paramref name="b"/> (a block dominates itself). Both must be reachable.</summary>
    public bool Dominates(int a, int b) => _in[a] <= _in[b] && _out[b] <= _out[a];

    /// <summary>Dominance frontier per block (reachable blocks only), in ascending block order.</summary>
    public List<int>[] Frontier()
    {
        if (_frontier is not null)
        {
            return _frontier;
        }

        int n = Blocks.Count;
        var df = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            df[i] = [];
        }

        for (int b = 0; b < n; b++)
        {
            if (!IsReachable(b))
            {
                continue;
            }

            int reachablePreds = 0;
            foreach (int p in Preds[b])
            {
                if (IsReachable(p))
                {
                    reachablePreds++;
                }
            }

            if (reachablePreds < 2)
            {
                continue;
            }

            foreach (int p in Preds[b])
            {
                if (!IsReachable(p))
                {
                    continue;
                }

                int runner = p;
                while (runner != Idom[b])
                {
                    if (!df[runner].Contains(b))
                    {
                        df[runner].Add(b);
                    }

                    runner = Idom[runner];
                }
            }
        }

        _frontier = df;
        return df;
    }

    private int Intersect(int a, int b)
    {
        while (a != b)
        {
            while (RpoNumber[a] > RpoNumber[b])
            {
                a = Idom[a];
            }

            while (RpoNumber[b] > RpoNumber[a])
            {
                b = Idom[b];
            }
        }

        return a;
    }
}
