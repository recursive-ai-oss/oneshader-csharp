using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;

namespace OneShader.Build.Tasks;

// Shader variants: keyword axes (item metadata Axes) and named define sets (OneShaderVariant items).
public sealed partial class CompileShaders
{
    private const string BadVariantsCode = "OSH0904";
    private const string TooManyVariantsCode = "OSH0905";
    private const string UnknownVariantShaderCode = "OSH0906";

    /// <summary>Default for the largest number of variants one shader may have.</summary>
    public const int DefaultVariantLimit = 64;

    /// <summary>
    /// The <c>OneShaderVariant</c> items: named define sets of a shader. The item name is the variant's name; metadata <c>Shader</c> is the
    /// <c>OneShader</c> item it belongs to (as written there, or any path to the same file) and <c>Defines</c> is <c>A;B=2</c>.
    /// </summary>
    public ITaskItem[] Variants { get; set; } = [];

    /// <summary>The largest number of variants one shader may have when its item has no <c>MaxVariants</c> metadata; empty means 64.</summary>
    public string DefaultMaxVariants { get; set; } = "";

    private sealed class AxisSpec
    {
        public string Name = "";

        /// <summary>A flag axis: defined (as 1) or not defined.</summary>
        public bool IsFlag;

        /// <summary>The values of a value axis.</summary>
        public List<string> Values = [];

        public int Count => IsFlag ? 2 : Values.Count;
    }

    private sealed class VariantSpec
    {
        public string Key = "";
        public string Name = "";

        /// <summary>The define set as shown in messages.</summary>
        public string Label = "";
        public string DefinesText = "";
        public List<int> Ordinals = [];
        public List<KeyValuePair<string, string?>> Defines = [];
    }

    private sealed class VariantPlan
    {
        public bool Named;
        public List<AxisSpec> Axes = [];
        public List<VariantSpec> Variants = [];

        public bool HasVariants => Variants.Count > 0;
    }

    /// <summary>Reads the item's variant declarations. Returns null (after logging) when they are invalid; a plan without variants for a plain shader.</summary>
    private VariantPlan? PlanVariants(ITaskItem item, string full, string relative, string projectDir)
    {
        var plan = new VariantPlan();
        string axesText = item.GetMetadata("Axes");
        var named = NamedVariantsOf(full, projectDir);
        bool hasAxes = axesText.Trim().Length > 0;
        if (!hasAxes && named.Count == 0)
        {
            return plan;
        }

        if (hasAxes && named.Count > 0)
        {
            Log.LogError(null, BadVariantsCode, null, full, 0, 0, 0, 0, "'" + relative + "' has both Axes metadata and OneShaderVariant items. Use one or the other: Axes for keyword combinations, OneShaderVariant items for hand-picked define sets.");
            return null;
        }

        int limit = DefaultVariantLimit;
        string limitText = item.GetMetadata("MaxVariants").Length > 0 ? item.GetMetadata("MaxVariants") : DefaultMaxVariants;
        if (limitText.Trim().Length > 0 && (!int.TryParse(limitText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
        {
            Log.LogError(null, BadMetadataCode, null, full, 0, 0, 0, 0, "Invalid MaxVariants '" + limitText + "'. Use a positive whole number, for example MaxVariants=\"128\" (or OneShaderMaxVariants in the project).");
            return null;
        }

        return hasAxes ? PlanAxes(plan, axesText, full, relative, limit) : PlanNamed(plan, named, full, relative, limit);
    }

    private VariantPlan? PlanNamed(VariantPlan plan, List<ITaskItem> named, string full, string relative, int limit)
    {
        plan.Named = true;
        if (named.Count > limit)
        {
            return TooMany(full, relative, named.Count.ToString(CultureInfo.InvariantCulture) + " OneShaderVariant items", named.Count, limit);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in named)
        {
            string name = v.ItemSpec.Trim();
            if (!IsIdentifier(name) || !names.Add(name))
            {
                Log.LogError(null, BadVariantsCode, null, full, 0, 0, 0, 0, "OneShaderVariant '" + v.ItemSpec + "' for '" + relative + "' is " + (names.Contains(name) ? "declared twice" : "not a valid name")
                    + ". Names are letters, digits and '_' (not starting with a digit) and unique per shader, for example <OneShaderVariant Include=\"Cutout\" Shader=\"" + relative + "\" Defines=\"ALPHA_TEST=1\" />.");
                return null;
            }

            var spec = new VariantSpec { Name = name };
            var texts = new List<string>();
            foreach (string d in Split(v.GetMetadata("Defines")))
            {
                int eq = d.IndexOf('=', StringComparison.Ordinal);
                spec.Defines.Add(eq < 0 ? new KeyValuePair<string, string?>(d, null) : new KeyValuePair<string, string?>(d.Substring(0, eq), d.Substring(eq + 1)));
                texts.Add(d);
            }

            spec.DefinesText = string.Join(" ", texts);
            spec.Label = "'" + name + "'" + (texts.Count > 0 ? " (" + string.Join(", ", texts) + ")" : "");
            spec.Key = ShortHash("name:" + name + ";" + string.Join(";", texts));
            plan.Variants.Add(spec);
        }

        return plan;
    }

    private VariantPlan? PlanAxes(VariantPlan plan, string axesText, string full, string relative, int limit)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in Split(axesText))
        {
            int eq = entry.IndexOf('=', StringComparison.Ordinal);
            var axis = new AxisSpec { Name = (eq < 0 ? entry : entry.Substring(0, eq)).Trim(), IsFlag = eq < 0 };
            string problem = "";
            if (!IsIdentifier(axis.Name))
            {
                problem = "'" + axis.Name + "' is not a valid macro name";
            }
            else if (!names.Add(axis.Name))
            {
                problem = "axis '" + axis.Name + "' is listed twice";
            }
            else if (!axis.IsFlag)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string raw in entry.Substring(eq + 1).Split('|'))
                {
                    string value = raw.Trim();
                    if (value.Length == 0 || !IsWord(value))
                    {
                        problem = "value '" + value + "' of axis '" + axis.Name + "' must be letters, digits and '_'";
                        break;
                    }

                    if (!seen.Add(value))
                    {
                        problem = "axis '" + axis.Name + "' lists value '" + value + "' twice";
                        break;
                    }

                    axis.Values.Add(value);
                }
            }

            if (problem.Length > 0)
            {
                Log.LogError(null, BadVariantsCode, null, full, 0, 0, 0, 0, "Invalid Axes entry '" + entry + "' for '" + relative + "': " + problem
                    + ". Write a flag as NAME and a choice as NAME=A|B|C, separated by ';', for example Axes=\"ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR\".");
                return null;
            }

            plan.Axes.Add(axis);
        }

        long count = 1;
        var shape = new StringBuilder();
        foreach (var axis in plan.Axes)
        {
            count = Math.Min(count * axis.Count, long.MaxValue / 1024);
            shape.Append(shape.Length == 0 ? "" : " x ").Append(axis.Name).Append(' ').Append(axis.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (count > limit)
        {
            return TooMany(full, relative, shape.ToString(), count, limit);
        }

        // The cartesian product, first axis varying slowest: an odometer over the axes' ordinals.
        var ordinals = new int[plan.Axes.Count];
        for (long n = 0; n < count; n++)
        {
            var spec = new VariantSpec();
            var texts = new List<string>();
            var keyParts = new List<string>();
            for (int a = 0; a < plan.Axes.Count; a++)
            {
                var axis = plan.Axes[a];
                int o = ordinals[a];
                spec.Ordinals.Add(o);
                if (axis.IsFlag)
                {
                    keyParts.Add(axis.Name + "=" + o.ToString(CultureInfo.InvariantCulture));
                    if (o == 1)
                    {
                        spec.Defines.Add(new KeyValuePair<string, string?>(axis.Name, "1"));
                        texts.Add(axis.Name + "=1");
                    }
                }
                else
                {
                    // A value axis defines the axis name as the value, and NAME_VALUE as 1 so '#if LIGHTING_PBR' reads naturally.
                    string value = axis.Values[o];
                    keyParts.Add(axis.Name + "=" + value);
                    spec.Defines.Add(new KeyValuePair<string, string?>(axis.Name, value));
                    spec.Defines.Add(new KeyValuePair<string, string?>(axis.Name + "_" + value, "1"));
                    texts.Add(axis.Name + "=" + value);
                }
            }

            spec.DefinesText = string.Join(" ", texts);
            spec.Label = texts.Count > 0 ? string.Join(", ", texts) : "no variant defines";
            spec.Key = ShortHash("axes:" + string.Join(";", keyParts));
            plan.Variants.Add(spec);
            for (int a = plan.Axes.Count - 1; a >= 0; a--)
            {
                if (++ordinals[a] < plan.Axes[a].Count)
                {
                    break;
                }

                ordinals[a] = 0;
            }
        }

        return plan;
    }

    private VariantPlan? TooMany(string full, string relative, string shape, long count, int limit)
    {
        Log.LogError(null, TooManyVariantsCode, null, full, 0, 0, 0, 0, "OneShader item '" + relative + "' declares " + count.ToString(CultureInfo.InvariantCulture) + " variants (" + shape + "), more than the limit of "
            + limit.ToString(CultureInfo.InvariantCulture) + ". Every variant is compiled for each selected target and embedded in the assembly. Remove an axis or a value, or raise the limit with MaxVariants=\""
            + Math.Max(limit * 2, 2).ToString(CultureInfo.InvariantCulture) + "\" on the item (or <OneShaderMaxVariants> in the project).");
        return null;
    }

    private List<ITaskItem> NamedVariantsOf(string full, string projectDir)
    {
        var result = new List<ITaskItem>();
        foreach (var v in Variants)
        {
            string shader = v.GetMetadata("Shader");
            if (shader.Length > 0 && string.Equals(Path.GetFullPath(shader, projectDir), full, StringComparison.Ordinal))
            {
                result.Add(v);
            }
        }

        return result;
    }

    /// <summary>Every OneShaderVariant must name a shader that is a OneShader item; a typo would otherwise silently produce no variants.</summary>
    private bool CheckVariantNames()
    {
        if (Variants.Length == 0)
        {
            return !Log.HasLoggedErrors;
        }

        string projectDir = Path.GetFullPath(ProjectDirectory);
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Shaders)
        {
            known.Add(Path.GetFullPath(item.GetMetadata("FullPath").Length > 0 ? item.GetMetadata("FullPath") : item.ItemSpec, projectDir));
        }

        bool ok = true;
        foreach (var v in Variants)
        {
            string shader = v.GetMetadata("Shader");
            if (shader.Length == 0 || !known.Contains(Path.GetFullPath(shader, projectDir)))
            {
                string file = v.GetMetadata("DefiningProjectFullPath");
                Log.LogError(null, UnknownVariantShaderCode, null, file.Length > 0 ? file : projectDir, 0, 0, 0, 0, "OneShaderVariant '" + v.ItemSpec + "' "
                    + (shader.Length == 0 ? "has no Shader metadata" : "names the shader '" + shader + "', which is not a OneShader item")
                    + ". Set Shader to the path of an item in the project, for example <OneShaderVariant Include=\"" + v.ItemSpec + "\" Shader=\"Shaders/Sprite.hlsl\" Defines=\"ALPHA_TEST=1\" />.");
                ok = false;
            }
        }

        return ok && !Log.HasLoggedErrors;
    }

    private static bool IsWord(string s)
    {
        foreach (char c in s)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    private static bool IsIdentifier(string s) => IsWord(s) && !char.IsAsciiDigit(s[0]);

    private static string ShortHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, 12).ToLowerInvariant();
}
