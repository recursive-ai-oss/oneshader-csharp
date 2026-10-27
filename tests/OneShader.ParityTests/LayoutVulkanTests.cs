using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Xunit;
using LayoutVk = OneShader.Layouts.Vulkan;
using LPipelineLayout = OneShader.Layouts.PipelineLayout;

namespace OneShader.ParityTests;

/// <summary>
/// Creates real Vulkan objects from the pipeline layout helpers' output: descriptor set layouts, a pipeline layout with the push-constant
/// range, descriptor sets, and graphics and compute pipelines whose shaders the validation layer checks against that layout. Any
/// validation message fails the test.
/// </summary>
public sealed unsafe class LayoutVulkanTests
{
    private static readonly string[] Gfx = ["VSMain", "PSMain"];
    private static readonly string[] Cs = ["CSMain"];

    public static TheoryData<string, string[]> Shaders() => new()
    {
        { "2d/sprite.hlsl", Gfx },
        { "2d/ui_rect.hlsl", Gfx },
        { "3d/push_lit.hlsl", Gfx },
        { "3d/pbr_forward.hlsl", Gfx },
        { "compute/histogram.hlsl", Cs },
        { "lang/resource_arrays.hlsl", Cs },
    };

    [Theory]
    [MemberData(nameof(Shaders))]
    public void PipelinesCreatedFromHelperLayoutsPassValidation(string rel, string[] entries)
    {
        var ctx = Gpu.RequireOrSkip();
        string path = Path.Combine(Tools.CorpusDir, rel);
        var result = ShaderCompiler.Compile(
            File.ReadAllText(path),
            Path.GetFileName(path),
            new CompileOptions { IncludeDirectories = [Path.GetDirectoryName(path)!] },
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var layouts = entries.Select(e =>
        {
            Assert.True(OneShader.Layouts.EntryLayout.TryCreate(result.Reflection!, e, out var l, out var error), error?.Message);
            return l!;
        }).ToArray();
        Assert.True(LPipelineLayout.TryCreate(layouts, out var pipeline, out var layoutError), layoutError?.Message);
        var info = pipeline!.ToVulkan();

        ctx.DrainMessages();
        var vk = ctx.Api;
        var dev = ctx.Device;
        var cleanup = new List<Action>();
        try
        {
            // One descriptor set layout per set, straight from the helper.
            var setLayouts = new DescriptorSetLayout[info.SetLayouts.Count];
            for (int s = 0; s < setLayouts.Length; s++)
            {
                var bindings = info.SetLayouts[s].Bindings.Select(b => new DescriptorSetLayoutBinding
                {
                    Binding = b.Binding,
                    DescriptorType = (DescriptorType)(uint)b.DescriptorType,
                    DescriptorCount = b.DescriptorCount,
                    StageFlags = (ShaderStageFlags)(uint)b.StageFlags,
                }).ToArray();
                fixed (DescriptorSetLayoutBinding* pb = bindings)
                {
                    var ci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)bindings.Length, PBindings = pb };
                    Assert.Equal(Result.Success, vk.CreateDescriptorSetLayout(dev, &ci, null, out setLayouts[s]));
                }

                var copy = setLayouts[s];
                cleanup.Add(() => vk.DestroyDescriptorSetLayout(dev, copy, null));
            }

            var ranges = info.PushConstantRanges.Select(r => new PushConstantRange { StageFlags = (ShaderStageFlags)(uint)r.StageFlags, Offset = r.Offset, Size = r.Size }).ToArray();
            Silk.NET.Vulkan.PipelineLayout vkLayout;
            fixed (DescriptorSetLayout* psl = setLayouts)
            fixed (PushConstantRange* ppr = ranges)
            {
                var pi = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)setLayouts.Length,
                    PSetLayouts = psl,
                    PushConstantRangeCount = (uint)ranges.Length,
                    PPushConstantRanges = ranges.Length > 0 ? ppr : null,
                };
                Assert.Equal(Result.Success, vk.CreatePipelineLayout(dev, &pi, null, out vkLayout));
            }

            cleanup.Add(() => vk.DestroyPipelineLayout(dev, vkLayout, null));

            // Descriptor sets can be allocated from those layouts with a pool sized from the same data.
            var sizes = info.SetLayouts.SelectMany(s => s.Bindings).GroupBy(b => b.DescriptorType)
                .Select(g => new DescriptorPoolSize((DescriptorType)(uint)g.Key, (uint)g.Sum(b => b.DescriptorCount))).ToArray();
            if (sizes.Length > 0)
            {
                DescriptorPool pool;
                fixed (DescriptorPoolSize* pps = sizes)
                {
                    var pci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)setLayouts.Length, PoolSizeCount = (uint)sizes.Length, PPoolSizes = pps };
                    Assert.Equal(Result.Success, vk.CreateDescriptorPool(dev, &pci, null, out pool));
                }

                cleanup.Add(() => vk.DestroyDescriptorPool(dev, pool, null));
                var sets = new DescriptorSet[setLayouts.Length];
                fixed (DescriptorSetLayout* psl = setLayouts)
                fixed (DescriptorSet* pds = sets)
                {
                    var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = (uint)sets.Length, PSetLayouts = psl };
                    Assert.Equal(Result.Success, vk.AllocateDescriptorSets(dev, &ai, pds));
                }
            }

            if (entries.Length == 1)
            {
                CreateComputePipeline(ctx, result, entries[0], vkLayout, cleanup);
            }
            else
            {
                CreateGraphicsPipeline(ctx, result, layouts, vkLayout, cleanup);
            }

            vk.DeviceWaitIdle(dev);
            Assert.Empty(ctx.DrainMessages());
        }
        finally
        {
            for (int i = cleanup.Count - 1; i >= 0; i--)
            {
                cleanup[i]();
            }
        }
    }

    private static ShaderModule Module(VkContext ctx, byte[] code, List<Action> cleanup)
    {
        fixed (byte* p = code)
        {
            var mi = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)p };
            Assert.Equal(Result.Success, ctx.Api.CreateShaderModule(ctx.Device, &mi, null, out ShaderModule module));
            cleanup.Add(() => ctx.Api.DestroyShaderModule(ctx.Device, module, null));
            return module;
        }
    }

    private static void CreateComputePipeline(VkContext ctx, CompileResult result, string entry, Silk.NET.Vulkan.PipelineLayout layout, List<Action> cleanup)
    {
        var module = Module(ctx, result.Find(entry, CompileTarget.SpirV)!.ToArray(), cleanup);
        nint name = Marshal.StringToHGlobalAnsi(entry);
        try
        {
            var ci = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Layout = layout,
                Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = (byte*)name },
            };
            Assert.Equal(Result.Success, ctx.Api.CreateComputePipelines(ctx.Device, default, 1, &ci, null, out Pipeline pipeline));
            cleanup.Add(() => ctx.Api.DestroyPipeline(ctx.Device, pipeline, null));
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    private static void CreateGraphicsPipeline(VkContext ctx, CompileResult result, OneShader.Layouts.EntryLayout[] entries, Silk.NET.Vulkan.PipelineLayout layout, List<Action> cleanup)
    {
        var vk = ctx.Api;
        var dev = ctx.Device;
        var vsEntry = entries.Single(e => e.Stage == OneShader.ShaderStage.Vertex);
        var psEntry = entries.Single(e => e.Stage == OneShader.ShaderStage.Pixel);
        var vs = Module(ctx, result.Find(vsEntry.Name, CompileTarget.SpirV)!.ToArray(), cleanup);
        var ps = Module(ctx, result.Find(psEntry.Name, CompileTarget.SpirV)!.ToArray(), cleanup);

        // The vertex input comes from the helper too; per-vertex data in vertex buffer binding 0.
        var vertex = vsEntry.Vertex!.ToVulkan();
        var bindingDescs = vertex.Bindings.Select(b => new VertexInputBindingDescription(b.Binding, b.Stride, (VertexInputRate)(uint)b.InputRate)).ToArray();
        var attrDescs = vertex.Attributes.Select(a => new VertexInputAttributeDescription(a.Location, a.Binding, (Format)(uint)a.Format, a.Offset)).ToArray();

        // A render pass with the one RGBA8 colour attachment every corpus pixel shader writes to.
        var attachment = new AttachmentDescription
        {
            Format = Format.R8G8B8A8Unorm,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef };
        var rpi = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &attachment, SubpassCount = 1, PSubpasses = &subpass };
        Assert.Equal(Result.Success, vk.CreateRenderPass(dev, &rpi, null, out RenderPass renderPass));
        cleanup.Add(() => vk.DestroyRenderPass(dev, renderPass, null));

        nint vsName = Marshal.StringToHGlobalAnsi(vsEntry.Name);
        nint psName = Marshal.StringToHGlobalAnsi(psEntry.Name);
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[]
            {
                new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vs, PName = (byte*)vsName },
                new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = ps, PName = (byte*)psName },
            };
            var viewport = new Viewport(0, 0, 16, 16, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(16, 16));
            var blendAttachment = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
            fixed (VertexInputBindingDescription* pbd = bindingDescs)
            fixed (VertexInputAttributeDescription* pad = attrDescs)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = (uint)bindingDescs.Length,
                    PVertexBindingDescriptions = pbd,
                    VertexAttributeDescriptionCount = (uint)attrDescs.Length,
                    PVertexAttributeDescriptions = pad,
                };
                var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
                var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, PViewports = &viewport, ScissorCount = 1, PScissors = &scissor };
                var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1f };
                var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
                var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blendAttachment };
                var depth = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
                var gci = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &assembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &raster,
                    PMultisampleState = &multisample,
                    PColorBlendState = &blend,
                    PDepthStencilState = &depth,
                    Layout = layout,
                    RenderPass = renderPass,
                };
                Assert.Equal(Result.Success, vk.CreateGraphicsPipelines(dev, default, 1, &gci, null, out Pipeline pipeline));
                cleanup.Add(() => vk.DestroyPipeline(dev, pipeline, null));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(vsName);
            Marshal.FreeHGlobal(psName);
        }
    }
}
