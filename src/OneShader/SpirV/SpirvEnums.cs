namespace OneShader.SpirV;

// The subset of the SPIR-V 1.5 numbers the backend uses. Values come from the machine-readable
// grammar published with the SPIR-V specification.

/// <summary>Opcodes.</summary>
internal static class Op
{
    public const uint Undef = 1, Source = 3, Name = 5, MemberName = 6, ExtInstImport = 11, ExtInst = 12, MemoryModel = 14,
        EntryPoint = 15, ExecutionMode = 16, Capability = 17, TypeVoid = 19, TypeBool = 20, TypeInt = 21, TypeFloat = 22,
        TypeVector = 23, TypeMatrix = 24, TypeImage = 25, TypeSampler = 26, TypeSampledImage = 27, TypeArray = 28,
        TypeRuntimeArray = 29, TypeStruct = 30, TypePointer = 32, TypeFunction = 33, ConstantTrue = 41, ConstantFalse = 42,
        Constant = 43, ConstantComposite = 44, ConstantNull = 46, Function = 54, FunctionParameter = 55, FunctionEnd = 56,
        FunctionCall = 57, Variable = 59, ImageTexelPointer = 60, Load = 61, Store = 62, AccessChain = 65, ArrayLength = 68, Decorate = 71,
        MemberDecorate = 72, VectorExtractDynamic = 77, VectorInsertDynamic = 78, VectorShuffle = 79,
        CompositeConstruct = 80, CompositeExtract = 81, CompositeInsert = 82, Transpose = 84, SampledImage = 86,
        ImageSampleImplicitLod = 87, ImageSampleExplicitLod = 88, ImageSampleDrefImplicitLod = 89,
        ImageSampleDrefExplicitLod = 90, ImageFetch = 95, ImageGather = 96, ImageDrefGather = 97, ImageRead = 98, ImageWrite = 99,
        Image = 100, ImageQuerySizeLod = 103, ImageQuerySize = 104, ImageQueryLod = 105, ImageQueryLevels = 106,
        ImageQuerySamples = 107, ConvertFToU = 109, ConvertFToS = 110, ConvertSToF = 111, ConvertUToF = 112,
        UConvert = 113, SConvert = 114, FConvert = 115, Bitcast = 124, SNegate = 126, FNegate = 127, IAdd = 128,
        FAdd = 129, ISub = 130, FSub = 131, IMul = 132, FMul = 133, UDiv = 134, SDiv = 135, FDiv = 136, UMod = 137,
        SRem = 138, FRem = 140, VectorTimesMatrix = 144, MatrixTimesVector = 145, MatrixTimesMatrix = 146,
        OuterProduct = 147, Dot = 148, Any = 154, All = 155, IsNan = 156, IsInf = 157, LogicalEqual = 164,
        LogicalNotEqual = 165, LogicalOr = 166, LogicalAnd = 167, LogicalNot = 168, Select = 169, IEqual = 170,
        INotEqual = 171, UGreaterThan = 172, SGreaterThan = 173, UGreaterThanEqual = 174, SGreaterThanEqual = 175,
        ULessThan = 176, SLessThan = 177, ULessThanEqual = 178, SLessThanEqual = 179, FOrdEqual = 180,
        FUnordNotEqual = 183, FOrdLessThan = 184, FOrdGreaterThan = 186, FOrdLessThanEqual = 188,
        FOrdGreaterThanEqual = 190, ShiftRightLogical = 194, ShiftRightArithmetic = 195, ShiftLeftLogical = 196,
        BitwiseOr = 197, BitwiseXor = 198, BitwiseAnd = 199, Not = 200, BitReverse = 204, BitCount = 205, DPdx = 207,
        DPdy = 208, Fwidth = 209, DPdxFine = 210, DPdyFine = 211, FwidthFine = 212, DPdxCoarse = 213, DPdyCoarse = 214,
        FwidthCoarse = 215, ControlBarrier = 224, MemoryBarrier = 225, AtomicExchange = 229, AtomicCompareExchange = 230,
        AtomicIAdd = 234, AtomicSMin = 236, AtomicUMin = 237, AtomicSMax = 238, AtomicUMax = 239, AtomicAnd = 240,
        AtomicOr = 241, AtomicXor = 242, Phi = 245, LoopMerge = 246, SelectionMerge = 247, Label = 248, Branch = 249,
        BranchConditional = 250, Switch = 251, Kill = 252, Return = 253, ReturnValue = 254, Unreachable = 255;
}

/// <summary>GLSL.std.450 extended instructions.</summary>
internal static class Glsl
{
    public const uint RoundEven = 2, Trunc = 3, FAbs = 4, SAbs = 5, FSign = 6, SSign = 7, Floor = 8, Ceil = 9, Fract = 10,
        Radians = 11, Degrees = 12, Sin = 13, Cos = 14, Tan = 15, Asin = 16, Acos = 17, Atan = 18, Sinh = 19, Cosh = 20,
        Tanh = 21, Atan2 = 25, Pow = 26, Exp = 27, Log = 28, Exp2 = 29, Log2 = 30, Sqrt = 31, InverseSqrt = 32,
        Determinant = 33, ModfStruct = 36, FMin = 37, UMin = 38, SMin = 39, FMax = 40, UMax = 41, SMax = 42,
        FClamp = 43, UClamp = 44, SClamp = 45, FMix = 46, Step = 48, SmoothStep = 49, Fma = 50, FrexpStruct = 52,
        PackHalf2x16 = 58, UnpackHalf2x16 = 62, Length = 66, Distance = 67, Cross = 68, Normalize = 69, FaceForward = 70,
        Reflect = 71, Refract = 72, FindILsb = 73, FindSMsb = 74, FindUMsb = 75;
}

/// <summary>Capabilities.</summary>
internal static class Cap
{
    public const uint Matrix = 0, Shader = 1, Geometry = 2, Float16 = 9, Float64 = 10, Int64 = 11, Int16 = 22, ClipDistance = 32, CullDistance = 33,
        ImageCubeArray = 34, SampleRateShading = 35, Sampled1D = 43, Image1D = 44, SampledCubeArray = 45,
        SampledBuffer = 46, ImageBuffer = 47, ImageMSArray = 48, StorageImageExtendedFormat = 49, ImageQuery = 50, DerivativeControl = 51,
        StorageImageReadWithoutFormat = 55, StorageImageWriteWithoutFormat = 56, ShaderLayer = 69, ShaderViewportIndex = 70;
}

/// <summary>Decorations.</summary>
internal static class Dec
{
    public const uint Block = 2, RowMajor = 4, ColMajor = 5, ArrayStride = 6, MatrixStride = 7, BuiltIn = 11,
        NoPerspective = 13, Flat = 14, Centroid = 16, Sample = 17, NonWritable = 24, Location = 30, Binding = 33,
        DescriptorSet = 34, Offset = 35, NoContraction = 42, NonUniform = 5300;
}

/// <summary>Built-in variable kinds.</summary>
internal static class Bi
{
    public const uint Position = 0, ClipDistance = 3, CullDistance = 4, PrimitiveId = 7, Layer = 9, ViewportIndex = 10,
        FragCoord = 15, FrontFacing = 17, SampleId = 18, SampleMask = 20, FragDepth = 22, WorkgroupId = 26,
        LocalInvocationId = 27, GlobalInvocationId = 28, LocalInvocationIndex = 29, VertexIndex = 42, InstanceIndex = 43;
}

/// <summary>Storage classes.</summary>
internal static class Sc
{
    public const uint UniformConstant = 0, Input = 1, Uniform = 2, Output = 3, Workgroup = 4, Private = 6, Function = 7,
        Image = 11, StorageBuffer = 12;
}
