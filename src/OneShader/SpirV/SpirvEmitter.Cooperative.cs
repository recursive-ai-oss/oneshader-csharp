using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.SpirV;

/// <summary>
/// Cooperative matrices (SPV_KHR_cooperative_matrix) and the Vulkan memory model they need. A module that uses them declares the
/// Vulkan memory model; every other module keeps GLSL450 and its output is unchanged.
/// </summary>
internal sealed partial class SpirvEmitter
{
    private const uint MemoryAccessNonPrivatePointer = 0x20;
    private const uint SemanticsMakeAvailable = 0x2000;
    private const uint SemanticsMakeVisible = 0x4000;

    private bool _vulkanMemoryModel;

    /// <summary>
    /// Switches the module to the Vulkan memory model when the entry point uses cooperative matrices. Under that model a load or store that
    /// other invocations can observe says so (NonPrivatePointer) and barriers make writes available and visible.
    /// </summary>
    private void DetectMemoryModel()
    {
        if ((WideTypeUse.Scan(_m, _ep).Features & DeviceFeatures.CooperativeMatrix) == 0)
        {
            return;
        }

        _vulkanMemoryModel = true;
        _s.VulkanMemoryModel = true;
        _s.Capabilities.Add(Cap.VulkanMemoryModel);
    }

    /// <summary>The memory-operand words for a load or store through <paramref name="pointer"/>: NonPrivatePointer for memory shared between invocations.</summary>
    private uint[] SharedMemoryAccess(IrValue pointer)
    {
        if (_vulkanMemoryModel && pointer.Type is PointerType { Space: AddressSpace.Workgroup or AddressSpace.StorageBuffer or AddressSpace.Uniform })
        {
            return [MemoryAccessNonPrivatePointer];
        }

        return [];
    }

    private void EmitCooperative(Instruction i)
    {
        switch (i.Intrinsic)
        {
            case Intrinsic.CoopMatrixLoad:
            {
                uint p = Ptr(i.Operands[0], out _);
                uint layout = UIntConst((uint)i.Immediates[0]);
                var access = SharedMemoryAccess(i.Operands[0]);
                var ops = new uint[3 + access.Length];
                ops[0] = p;
                ops[1] = layout;
                ops[2] = Arg(i, 1);
                access.CopyTo(ops, 3);
                Direct(i, Op.CooperativeMatrixLoad, TypeId(i.Type), ops);
                break;
            }

            case Intrinsic.CoopMatrixStore:
            {
                uint p = Ptr(i.Operands[0], out _);
                var access = SharedMemoryAccess(i.Operands[0]);
                var ops = new uint[4 + access.Length];
                ops[0] = p;
                ops[1] = Arg(i, 1);
                ops[2] = UIntConst((uint)i.Immediates[0]);
                ops[3] = Arg(i, 2);
                access.CopyTo(ops, 4);
                SpirvModule.Put(_s.Code, Op.CooperativeMatrixStore, ops);
                break;
            }

            case Intrinsic.CoopMatrixMulAdd:
                Direct(i, Op.CooperativeMatrixMulAdd, TypeId(i.Type), Arg(i, 0), Arg(i, 1), Arg(i, 2));
                break;
            case Intrinsic.CoopMatrixLength:
                Direct(i, Op.CooperativeMatrixLength, TypeId(i.Type), TypeId(i.Operands[0].Type));
                break;
            default:
            {
                // Zero: one scalar constituent fills every element (OpCompositeConstruct, SPV_KHR_cooperative_matrix).
                var cm = (CooperativeMatrixType)i.Type;
                Direct(i, Op.CompositeConstruct, TypeId(cm), ConstId(Constant.FromBits(cm.Element, 0)));
                break;
            }
        }
    }
}
