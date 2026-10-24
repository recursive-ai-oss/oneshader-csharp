using System;
using System.Collections.Generic;

namespace OneShader.Types;

/// <summary>Recognizes the names of HLSL's built-in types (<c>float3</c>, <c>int2x2</c>, <c>Texture2D</c>, ...).</summary>
internal static class BuiltinTypes
{
    private static readonly Dictionary<string, ScalarKind> ScalarNames = new(StringComparer.Ordinal)
    {
        ["bool"] = ScalarKind.Bool,
        ["int"] = ScalarKind.Int,
        ["uint"] = ScalarKind.UInt,
        ["dword"] = ScalarKind.UInt,
        ["half"] = ScalarKind.Half,
        ["float"] = ScalarKind.Float,
        ["double"] = ScalarKind.Double,
        ["min16float"] = ScalarKind.Min16Float,
        ["min10float"] = ScalarKind.Min10Float,
        ["min16int"] = ScalarKind.Min16Int,
        ["min16uint"] = ScalarKind.Min16UInt,
        ["int16_t"] = ScalarKind.Int16,
        ["uint16_t"] = ScalarKind.UInt16,
        ["int64_t"] = ScalarKind.Int64,
        ["uint64_t"] = ScalarKind.UInt64,
        ["float16_t"] = ScalarKind.Half,
        ["float32_t"] = ScalarKind.Float,
        ["float64_t"] = ScalarKind.Double,
        ["int32_t"] = ScalarKind.Int,
        ["uint32_t"] = ScalarKind.UInt,
    };

    private static readonly Dictionary<string, ResourceTypeKind> ResourceNames = new(StringComparer.Ordinal)
    {
        ["Texture1D"] = ResourceTypeKind.Texture1D,
        ["Texture1DArray"] = ResourceTypeKind.Texture1DArray,
        ["Texture2D"] = ResourceTypeKind.Texture2D,
        ["Texture2DArray"] = ResourceTypeKind.Texture2DArray,
        ["Texture2DMS"] = ResourceTypeKind.Texture2DMS,
        ["Texture2DMSArray"] = ResourceTypeKind.Texture2DMSArray,
        ["Texture3D"] = ResourceTypeKind.Texture3D,
        ["TextureCube"] = ResourceTypeKind.TextureCube,
        ["TextureCubeArray"] = ResourceTypeKind.TextureCubeArray,
        ["RWTexture1D"] = ResourceTypeKind.RWTexture1D,
        ["RWTexture1DArray"] = ResourceTypeKind.RWTexture1DArray,
        ["RWTexture2D"] = ResourceTypeKind.RWTexture2D,
        ["RWTexture2DArray"] = ResourceTypeKind.RWTexture2DArray,
        ["RWTexture3D"] = ResourceTypeKind.RWTexture3D,
        ["Buffer"] = ResourceTypeKind.Buffer,
        ["RWBuffer"] = ResourceTypeKind.RWBuffer,
        ["StructuredBuffer"] = ResourceTypeKind.StructuredBuffer,
        ["RWStructuredBuffer"] = ResourceTypeKind.RWStructuredBuffer,
        ["AppendStructuredBuffer"] = ResourceTypeKind.AppendStructuredBuffer,
        ["ConsumeStructuredBuffer"] = ResourceTypeKind.ConsumeStructuredBuffer,
        ["ByteAddressBuffer"] = ResourceTypeKind.ByteAddressBuffer,
        ["RWByteAddressBuffer"] = ResourceTypeKind.RWByteAddressBuffer,
        ["ConstantBuffer"] = ResourceTypeKind.ConstantBuffer,
    };

    public static bool TryGetScalar(string name, out ScalarType scalar)
    {
        if (ScalarNames.TryGetValue(name, out var k))
        {
            scalar = ScalarType.Get(k);
            return true;
        }

        scalar = null!;
        return false;
    }

    public static bool TryGetResource(string name, out ResourceTypeKind kind) => ResourceNames.TryGetValue(name, out kind);

    /// <summary>Resolves a name such as <c>float</c>, <c>uint3</c>, <c>float4x4</c>, <c>SamplerState</c> or a resource name without template arguments.</summary>
    public static bool TryGet(string name, out HlslType type)
    {
        if (TryGetScalar(name, out var s))
        {
            type = s;
            return true;
        }

        switch (name)
        {
            case "void":
                type = HlslType.Void;
                return true;
            case "SamplerState" or "sampler" or "sampler2D":
                type = SamplerType.State;
                return true;
            case "SamplerComparisonState":
                type = SamplerType.Comparison;
                return true;
        }

        if (TryGetVectorOrMatrix(name, out var vm))
        {
            type = vm;
            return true;
        }

        if (ResourceNames.TryGetValue(name, out var rk))
        {
            type = new ResourceType(rk, DefaultElement(rk));
            return true;
        }

        type = null!;
        return false;
    }

    /// <summary>The element type a resource has when written without template arguments.</summary>
    public static HlslType? DefaultElement(ResourceTypeKind kind) => kind switch
    {
        ResourceTypeKind.ByteAddressBuffer or ResourceTypeKind.RWByteAddressBuffer or ResourceTypeKind.ConstantBuffer => null,
        ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer or ResourceTypeKind.AppendStructuredBuffer
            or ResourceTypeKind.ConsumeStructuredBuffer => null,
        _ => HlslType.Vector(ScalarType.Float, 4),
    };

    public static bool TryGetVectorOrMatrix(string name, out HlslType type)
    {
        type = null!;
        int n = name.Length;
        if (n < 2)
        {
            return false;
        }

        char last = name[n - 1];
        if (last is < '1' or > '4')
        {
            return false;
        }

        // floatN or floatNxM: strip the digits, look up the scalar.
        int baseEnd = n - 1;
        int rows = 0;
        int cols = last - '0';
        if (n >= 4 && name[n - 2] == 'x' && name[n - 3] is >= '1' and <= '4')
        {
            rows = name[n - 3] - '0';
            baseEnd = n - 3;
        }

        if (baseEnd == 0 || !ScalarNames.TryGetValue(name.Substring(0, baseEnd), out var k))
        {
            return false;
        }

        var scalar = ScalarType.Get(k);
        type = rows == 0 ? HlslType.Vector(scalar, cols) : HlslType.Matrix(scalar, rows, cols);
        return true;
    }

    /// <summary>True for every name the parser must treat as a type without having seen a declaration.</summary>
    public static bool IsTypeName(string name) =>
        ScalarNames.ContainsKey(name) || ResourceNames.ContainsKey(name) || TryGetVectorOrMatrix(name, out _)
        || name is "void" or "vector" or "matrix" or "SamplerState" or "SamplerComparisonState" or "sampler" or "string";

    /// <summary>True for built-in names that take template arguments (<c>vector</c>, <c>Texture2D</c>, ...).</summary>
    public static bool IsTemplateTypeName(string name) => name is "vector" or "matrix" || ResourceNames.ContainsKey(name);
}
