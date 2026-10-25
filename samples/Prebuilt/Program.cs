using System;
using System.Numerics;
using System.Runtime.InteropServices;
using PrebuiltSample.Shaders;

// Checks the generated code against the numbers the shaders were compiled with. Exit code 0 = all good.
int failures = 0;
void Check(string what, bool ok)
{
    Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    if (!ok)
    {
        failures++;
    }
}

Check("Sprite.Frame is 64 bytes", Marshal.SizeOf<Sprite.Frame>() == 64 && Sprite.Frame.Size == 64);
Check("vertex stride is 32", Sprite.VSMain.Stride == 32 && Marshal.SizeOf<Sprite.VSMain.Vertex>() == 32);
Check("Color is at offset 16", Marshal.OffsetOf<Sprite.VSMain.Vertex>(nameof(Sprite.VSMain.Vertex.Color)) == 16);
Check("three vertex attributes", Sprite.VSMain.Inputs.Length == 3 && Sprite.VSMain.Inputs[2].Offset == 16);
Check("Atlas is set 0, binding 1", Sprite.Bindings.Atlas.Set == 0 && Sprite.Bindings.Atlas.Binding == 1);
Check("three resources listed", Sprite.Bindings.All.Length == 3);
Check("SPIR-V starts with the magic number", Sprite.VSMain.SpirV.Length > 4 && Sprite.VSMain.SpirV[0] == 0x03 && Sprite.VSMain.SpirV[3] == 0x07);
Check("compute group size is 8x8x1", Particles.CSMain.ThreadGroupSizeX == 8 && Particles.CSMain.ThreadGroupSizeY == 8 && Particles.CSMain.ThreadGroupSizeZ == 1);
Check("Textures is an array of 4 in one binding", Pool.Bindings.Textures.Count == 4 && Pool.Bindings.Textures.Binding == 0 && Pool.Bindings.Samplers.Count == 2);
Check("append buffer counter is binding 3 of the same set", Pool.Bindings.Hits.CounterBinding == 3 && Pool.Bindings.Hits.Set == 0 && Pool.Bindings.Work.CounterBinding == 5);
Check("counters are listed after their buffers", Pool.Bindings.All.Length == 6 && Pool.Bindings.All[3].Name == "Hits.counter" && Pool.Bindings.All[3].Binding == 3);

// HLSL's M[0][3] (translation in a column-vector convention) is M14 in C# whatever the packing.
var frame = new Sprite.Frame { ViewProjection = Matrix4x4.Identity };
frame.ViewProjection = new Matrix4x4(1, 0, 0, 5, 0, 1, 0, 6, 0, 0, 1, 7, 0, 0, 0, 1);
Check("matrix element (0,3) round-trips", frame.ViewProjection.M14 == 5 && frame.ViewProjection.M24 == 6);
var bytes = new byte[Sprite.Frame.Size];
MemoryMarshal.Write(bytes, in frame);
Check("column-major storage puts M[0][3] in the fourth column", BitConverter.ToSingle(bytes, 48) == 5 && BitConverter.ToSingle(bytes, 52) == 6);

return failures;
