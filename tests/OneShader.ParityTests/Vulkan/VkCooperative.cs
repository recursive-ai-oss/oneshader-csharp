using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OneShader.ParityTests;

// Vulkan structures for cooperative matrices and bfloat16, declared here because the Vulkan binding in use predates VK_KHR_shader_bfloat16.

/// <summary>VkPhysicalDeviceCooperativeMatrixFeaturesKHR.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CoopFeatures
{
    public const int StructType = 1000506000;
    public int SType;
    public void* PNext;
    public uint CooperativeMatrix;
    public uint CooperativeMatrixRobustBufferAccess;
}

/// <summary>VkPhysicalDeviceShaderBfloat16FeaturesKHR.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Bf16Features
{
    public const int StructType = 1000141000;
    public int SType;
    public void* PNext;
    public uint ShaderBFloat16Type;
    public uint ShaderBFloat16DotProduct;
    public uint ShaderBFloat16CooperativeMatrix;
}

/// <summary>VkPhysicalDeviceSubgroupSizeControlFeaturesEXT (full-subgroup pipelines: SPIR-V below 1.6 may use cooperative matrices only that way).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SubgroupControlFeatures
{
    public const int StructType = 1000225002;
    public int SType;
    public void* PNext;
    public uint SubgroupSizeControl;
    public uint ComputeFullSubgroups;
}

/// <summary>VkCooperativeMatrixPropertiesKHR.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CoopProperties
{
    public const int StructType = 1000506001;
    public int SType;
    public void* PNext;
    public uint MSize;
    public uint NSize;
    public uint KSize;
    public uint AType;
    public uint BType;
    public uint CType;
    public uint ResultType;
    public uint SaturatingAccumulation;
    public uint Scope;
}

/// <summary>One shape/type combination a device supports for subgroup-scope cooperative matrices.</summary>
internal readonly record struct CoopShape(uint M, uint N, uint K, uint AType, uint BType, uint CType, uint ResultType)
{
    internal const uint Float16 = 0;
    internal const uint Float32 = 1;
    internal const uint BFloat16 = 1000141000;

    private static string Name(uint t) => t switch
    {
        Float16 => "f16",
        Float32 => "f32",
        BFloat16 => "bf16",
        3 => "s8",
        4 => "s16",
        5 => "s32",
        _ => "type" + t,
    };

    public override string ToString() => $"{M}x{N}x{K} {Name(AType)}*{Name(BType)}+{Name(CType)}->{Name(ResultType)}";
}

public sealed partial class VkContext
{
    /// <summary>Queries vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR and keeps the subgroup-scope entries; empty when the extension is not enabled.</summary>
    internal unsafe IReadOnlyList<CoopShape> CooperativeShapes()
    {
        var list = new List<CoopShape>();
        if (!CooperativeMatrixFeature)
        {
            return list;
        }
        nint name = Marshal.StringToHGlobalAnsi("vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR");
        try
        {
            nint proc = (nint)Api.GetInstanceProcAddr(Instance, (byte*)name);
            if (proc == 0)
            {
                return list;
            }
            var fn = (delegate* unmanaged<nint, uint*, CoopProperties*, int>)proc;
            uint count = 0;
            if (fn(Physical.Handle, &count, null) != 0 || count == 0)
            {
                return list;
            }
            var props = new CoopProperties[count];
            for (int i = 0; i < props.Length; i++)
            {
                props[i].SType = CoopProperties.StructType;
            }
            fixed (CoopProperties* p = props)
            {
                if (fn(Physical.Handle, &count, p) < 0)
                {
                    return list;
                }
            }
            for (int i = 0; i < count; i++)
            {
                if (props[i].Scope == 3)
                {
                    list.Add(new CoopShape(props[i].MSize, props[i].NSize, props[i].KSize, props[i].AType, props[i].BType, props[i].CType, props[i].ResultType));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
        return list;
    }
}
