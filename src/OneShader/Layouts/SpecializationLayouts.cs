using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Types;

namespace OneShader.Layouts
{
    /// <summary>
    /// One specialization constant (<c>[[vk::constant_id(N)]] const T Name = default;</c>) that an entry point reads.
    /// </summary>
    /// <param name="Name">The HLSL name.</param>
    /// <param name="Id">The <c>constant_id</c>: SPIR-V <c>SpecId</c>, Metal <c>function_constant</c> index, and the decimal key of WebGPU's <c>constants</c> record.</param>
    /// <param name="Type"><see cref="ScalarKind.Bool"/>, <see cref="ScalarKind.Int"/>, <see cref="ScalarKind.UInt"/> or <see cref="ScalarKind.Float"/>.</param>
    /// <param name="DefaultBits">The default as a 32-bit pattern: 0 or 1 for bool, two's complement for int, IEEE bits for float.</param>
    public readonly record struct SpecConstantLayout(string Name, uint Id, ScalarKind Type, uint DefaultBits)
    {
        /// <summary>The default as a double (bool as 0 or 1); exact for all four types.</summary>
        public double DefaultValue => SpecializationValues.ToDouble(Type, DefaultBits);
    }

    /// <summary>
    /// The values a host gives an entry point's specialization constants, and their conversion to what each graphics API takes:
    /// Vulkan <c>VkSpecializationInfo</c> contents, Metal function-constant values and WebGPU's <c>constants</c> record.
    /// Every constant starts at its default; <see cref="Set(string, bool)"/> and its overloads override it. Plain data: no graphics
    /// API is referenced.
    /// </summary>
    public sealed class SpecializationValues
    {
        private readonly SpecConstantLayout[] _constants;
        private readonly uint[] _bits;

        /// <summary>Creates values for the given constants, all at their defaults.</summary>
        /// <param name="constants">The constants, for example <see cref="EntryLayout.SpecConstants"/>.</param>
        public SpecializationValues(IReadOnlyList<SpecConstantLayout> constants)
        {
            ArgumentNullException.ThrowIfNull(constants);
            _constants = new SpecConstantLayout[constants.Count];
            _bits = new uint[constants.Count];
            for (int i = 0; i < _constants.Length; i++)
            {
                _constants[i] = constants[i];
                _bits[i] = constants[i].DefaultBits;
            }
        }

        /// <summary>Creates values for the constants <paramref name="entry"/> reads, all at their defaults.</summary>
        /// <param name="entry">The entry point's layout.</param>
        /// <returns>The values.</returns>
        public static SpecializationValues For(EntryLayout entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            return new SpecializationValues(entry.SpecConstants);
        }

        /// <summary>The constants, in declaration order.</summary>
        public IReadOnlyList<SpecConstantLayout> Constants => _constants;

        /// <summary>Overrides a <c>bool</c> constant.</summary>
        /// <param name="name">The HLSL name.</param>
        /// <param name="value">The value.</param>
        /// <returns>This object, for chaining.</returns>
        /// <exception cref="ArgumentException">There is no constant of that name, or it is not a <c>bool</c>.</exception>
        public SpecializationValues Set(string name, bool value) => Put(name, ScalarKind.Bool, value ? 1u : 0u);

        /// <summary>Overrides an <c>int</c> constant.</summary>
        /// <param name="name">The HLSL name.</param>
        /// <param name="value">The value.</param>
        /// <returns>This object, for chaining.</returns>
        /// <exception cref="ArgumentException">There is no constant of that name, or it is not an <c>int</c>.</exception>
        public SpecializationValues Set(string name, int value) => Put(name, ScalarKind.Int, unchecked((uint)value));

        /// <summary>Overrides a <c>uint</c> constant.</summary>
        /// <param name="name">The HLSL name.</param>
        /// <param name="value">The value.</param>
        /// <returns>This object, for chaining.</returns>
        /// <exception cref="ArgumentException">There is no constant of that name, or it is not a <c>uint</c>.</exception>
        public SpecializationValues Set(string name, uint value) => Put(name, ScalarKind.UInt, value);

        /// <summary>Overrides a <c>float</c> constant.</summary>
        /// <param name="name">The HLSL name.</param>
        /// <param name="value">The value.</param>
        /// <returns>This object, for chaining.</returns>
        /// <exception cref="ArgumentException">There is no constant of that name, or it is not a <c>float</c>.</exception>
        public SpecializationValues Set(string name, float value) => Put(name, ScalarKind.Float, BitConverter.SingleToUInt32Bits(value));

        /// <summary>The current value of a constant as a double (bool as 0 or 1).</summary>
        /// <param name="name">The HLSL name.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">There is no constant of that name.</exception>
        public double ValueOf(string name)
        {
            int i = IndexOf(name);
            return ToDouble(_constants[i].Type, _bits[i]);
        }

        /// <summary>
        /// Vulkan: one map entry per constant (offset 4 × position, size 4: a <c>bool</c> is a 32-bit <c>VkBool32</c>) and the data they
        /// point into. Pass them as the <c>pMapEntries</c> and <c>pData</c> of the stage's <c>VkSpecializationInfo</c>.
        /// </summary>
        /// <returns>The entries and data.</returns>
        public Vulkan.SpecializationInfo ToVulkan()
        {
            var entries = new Vulkan.SpecializationMapEntry[_constants.Length];
            var data = new byte[_constants.Length * 4];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new Vulkan.SpecializationMapEntry(_constants[i].Id, (uint)(i * 4), 4);
                BitConverter.TryWriteBytes(data.AsSpan(i * 4, 4), _bits[i]);
            }

            return new Vulkan.SpecializationInfo(entries, data);
        }

        /// <summary>Metal: one value per constant, for <c>MTLFunctionConstantValues.setConstantValue(_:type:index:)</c>.</summary>
        /// <returns>The values in declaration order.</returns>
        public IReadOnlyList<Metal.FunctionConstantValue> ToMetal()
        {
            var list = new Metal.FunctionConstantValue[_constants.Length];
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = new Metal.FunctionConstantValue(_constants[i].Id, Metal.FunctionConstantType.FromScalar(_constants[i].Type), _bits[i]);
            }

            return list;
        }

        /// <summary>
        /// WebGPU: the <c>constants</c> record of a programmable stage. Keys are the decimal constant ids (what <c>@id(n)</c> declares);
        /// values are numbers (<c>bool</c> as 0 or 1), as the API takes them.
        /// </summary>
        /// <returns>The record in declaration order.</returns>
        public IReadOnlyDictionary<string, double> ToWebGpu()
        {
            var map = new Dictionary<string, double>(_constants.Length, StringComparer.Ordinal);
            for (int i = 0; i < _constants.Length; i++)
            {
                map[_constants[i].Id.ToString(CultureInfo.InvariantCulture)] = ToDouble(_constants[i].Type, _bits[i]);
            }

            return map;
        }

        internal static double ToDouble(ScalarKind type, uint bits) => type switch
        {
            ScalarKind.Float => BitConverter.UInt32BitsToSingle(bits),
            ScalarKind.Int => unchecked((int)bits),
            _ => bits,
        };

        private int IndexOf(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            for (int i = 0; i < _constants.Length; i++)
            {
                if (_constants[i].Name == name)
                {
                    return i;
                }
            }

            throw new ArgumentException("The entry point reads no specialization constant named '" + name + "'. Check the HLSL name and that the entry point (not only another one in the file) uses it.", nameof(name));
        }

        private SpecializationValues Put(string name, ScalarKind type, uint bits)
        {
            int i = IndexOf(name);
            if (_constants[i].Type != type)
            {
                throw new ArgumentException("The specialization constant '" + name + "' is a " + _constants[i].Type + "; set it with a value of that type.", nameof(name));
            }

            _bits[i] = bits;
            return this;
        }
    }
}

namespace OneShader.Layouts.Vulkan
{
    /// <summary>A <c>VkSpecializationMapEntry</c>: where in the data block one constant's value sits.</summary>
    /// <param name="ConstantId">The <c>constantID</c> (the shader's <c>SpecId</c>).</param>
    /// <param name="Offset">Byte offset of the value in the data block.</param>
    /// <param name="Size">Byte size of the value (4 for bool, int, uint and float; <c>VkBool32</c> for bool).</param>
    public readonly record struct SpecializationMapEntry(uint ConstantId, uint Offset, uint Size);

    /// <summary>The contents of a <c>VkSpecializationInfo</c>: map entries and the data they index.</summary>
    public sealed class SpecializationInfo
    {
        /// <summary>Creates the description.</summary>
        /// <param name="mapEntries">The entries.</param>
        /// <param name="data">The data block.</param>
        public SpecializationInfo(IReadOnlyList<SpecializationMapEntry> mapEntries, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(mapEntries);
            ArgumentNullException.ThrowIfNull(data);
            MapEntries = mapEntries;
            Data = data;
        }

        /// <summary>The entries, in declaration order.</summary>
        public IReadOnlyList<SpecializationMapEntry> MapEntries { get; }

        /// <summary>The data block the entries index (little-endian, as every Vulkan host is).</summary>
        public byte[] Data { get; }
    }
}

namespace OneShader.Layouts.Metal
{
    /// <summary>The scalar type of a Metal function constant, with the numeric values of <c>MTLDataType</c>.</summary>
    public readonly record struct FunctionConstantType
    {
        private FunctionConstantType(uint dataType)
        {
            DataType = dataType;
        }

        /// <summary><c>MTLDataType.float</c>.</summary>
        public static FunctionConstantType Float { get; } = new(3);

        /// <summary><c>MTLDataType.int</c>.</summary>
        public static FunctionConstantType Int { get; } = new(29);

        /// <summary><c>MTLDataType.uint</c>.</summary>
        public static FunctionConstantType UInt { get; } = new(33);

        /// <summary><c>MTLDataType.bool</c>.</summary>
        public static FunctionConstantType Bool { get; } = new(53);

        /// <summary>The raw <c>MTLDataType</c> value.</summary>
        public uint DataType { get; }

        internal static FunctionConstantType FromScalar(ScalarKind kind) => kind switch
        {
            ScalarKind.Bool => Bool,
            ScalarKind.Int => Int,
            ScalarKind.UInt => UInt,
            _ => Float,
        };
    }

    /// <summary>One value for <c>MTLFunctionConstantValues</c>.</summary>
    /// <param name="Index">The <c>[[function_constant(index)]]</c> index (the HLSL <c>constant_id</c>).</param>
    /// <param name="Type">The data type to pass.</param>
    /// <param name="Bits">The 32-bit pattern: write these four bytes (a Metal <c>bool</c> is one byte: write the low byte).</param>
    public readonly record struct FunctionConstantValue(uint Index, FunctionConstantType Type, uint Bits)
    {
        /// <summary>The value as a boolean (for a bool constant).</summary>
        public bool AsBool => Bits != 0;

        /// <summary>The value as an int.</summary>
        public int AsInt => unchecked((int)Bits);

        /// <summary>The value as a float.</summary>
        public float AsFloat => BitConverter.UInt32BitsToSingle(Bits);
    }
}
