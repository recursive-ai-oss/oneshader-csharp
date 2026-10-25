using System;
using System.Collections.Generic;

namespace OneShader.ParityTests;

/// <summary>All input bytes of a case, materialized once from its description.</summary>
public sealed class CaseData
{
    /// <summary>Buffer contents by resource index in <see cref="CaseModel.Resources"/>, padded to the declared size.</summary>
    public Dictionary<int, byte[]> Buffers { get; } = new();

    /// <summary>Encoded texels by resource index (textures and storage images).</summary>
    public Dictionary<int, byte[]> Images { get; } = new();

    /// <summary>Vertex buffer bytes by binding.</summary>
    public Dictionary<int, byte[]> VertexBuffers { get; } = new();

    public byte[]? Indices { get; private set; }

    /// <summary>Push-constant bytes (a multiple of 4 long), or null when the case has none.</summary>
    public byte[]? Push { get; private set; }

    /// <summary>Encoded initial target contents in the target format (graphics cases).</summary>
    public byte[]? Background { get; private set; }

    /// <summary>Builds the data for <paramref name="model"/>.</summary>
    public static CaseData Build(CaseModel model)
    {
        var d = new CaseData();
        for (int i = 0; i < model.Resources.Count; i++)
        {
            var r = model.Resources[i];
            switch (r.Kind)
            {
                case ResourceKind.Uniform:
                case ResourceKind.Storage:
                    {
                        byte[] raw = r.Data is null ? [] : DataGen.Materialize(r.Data);
                        int size = Math.Max(Math.Max(raw.Length, r.SizeBytes), 16);
                        size = (size + 15) & ~15;
                        byte[] padded = new byte[size];
                        raw.CopyTo(padded, 0);
                        d.Buffers[i] = padded;
                        break;
                    }
                case ResourceKind.Texture:
                case ResourceKind.StorageImage:
                    {
                        // Mip levels are stored one after another, each generated at its own size.
                        var all = new List<byte>();
                        for (int level = 0; level < r.MipLevels; level++)
                        {
                            int w = Math.Max(r.Width >> level, 1);
                            int h = Math.Max(r.Height >> level, 1);
                            all.AddRange(ImageGen.Encode(ImageGen.Generate(r.Generator, w, h, r.Layers, level, r.MipLevels), r.Format));
                        }
                        d.Images[i] = all.ToArray();
                        break;
                    }
            }
        }
        if (model.Push is not null)
        {
            d.Push = DataGen.Materialize(model.Push);
        }
        var g = model.Graphics;
        if (g is not null)
        {
            foreach (var vb in g.VertexBuffers)
            {
                d.VertexBuffers[vb.Binding] = DataGen.Materialize(vb.Data);
            }
            if (g.IndexData is not null)
            {
                d.Indices = DataGen.Materialize(g.IndexData, g.IndexIs16Bit ? 2 : 4);
            }
            d.Background = ImageGen.Encode(
                ImageGen.Generate(g.Background.Length == 0 ? "solid(0,0,0,0)" : g.Background, g.Width, g.Height, 0),
                g.TargetFormat);
        }
        return d;
    }
}
