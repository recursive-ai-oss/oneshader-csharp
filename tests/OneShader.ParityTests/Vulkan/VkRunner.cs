using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkSampler = Silk.NET.Vulkan.Sampler;

namespace OneShader.ParityTests;

/// <summary>One value read back from the GPU after a run.</summary>
public sealed class OutputBlob
{
    public OutputBlob(string key, byte[] bytes, int width, int height, PixelFormat format, bool isImage)
    {
        Key = key;
        Bytes = bytes;
        Width = width;
        Height = height;
        Format = format;
        IsImage = isImage;
    }

    /// <summary><c>buffer:SET.BINDING</c>, <c>image:SET.BINDING</c> or <c>target</c>.</summary>
    public string Key { get; }

    public byte[] Bytes { get; }

    public int Width { get; }

    public int Height { get; }

    public PixelFormat Format { get; }

    public bool IsImage { get; }
}

/// <summary>Everything a run produced: outputs in declaration order plus validation messages.</summary>
public sealed class RunResult
{
    public RunResult(List<OutputBlob> outputs, IReadOnlyList<string> validationMessages)
    {
        Outputs = outputs;
        ValidationMessages = validationMessages;
    }

    public List<OutputBlob> Outputs { get; }

    public IReadOnlyList<string> ValidationMessages { get; }

    public OutputBlob Get(string key) => Outputs.First(o => o.Key == key);
}

/// <summary>
/// Runs a case on the GPU: a compute dispatch sequence over storage buffers and images, or one
/// offscreen draw into an RGBA8 image, then reads the outputs back.
/// </summary>
public static unsafe class VkRunner
{
    /// <summary>
    /// Runs <paramref name="model"/> with SPIR-V supplied by <paramref name="spirv"/> (entry point name, stage).
    /// With <paramref name="yUp"/> the draw uses a negative-height viewport and clockwise front faces, which puts clip-space
    /// y up the way D3D, Metal and WebGPU do; the default keeps Vulkan's native y-down orientation.
    /// </summary>
    public static RunResult Run(VkContext ctx, CaseModel model, CaseData data, Func<string, ShaderStage, byte[]> spirv, bool yUp = false,
        Func<string, global::OneShader.Layouts.Vulkan.SpecializationInfo?>? specialization = null)
    {
        ctx.DrainMessages();
        using var run = new RunState(ctx, model, data, spirv) { YUp = yUp, Specialization = specialization };
        var outputs = run.Execute();
        ctx.Api.DeviceWaitIdle(ctx.Device);
        return new RunResult(outputs, ctx.DrainMessages());
    }

    /// <summary>Reads the name of the first OpEntryPoint of a SPIR-V module.</summary>
    internal static string EntryPointName(byte[] spirv)
    {
        // Header is 5 words; each instruction starts with (wordCount << 16 | opcode). OpEntryPoint = 15:
        // execution model, id, then the name as a nul-terminated UTF-8 literal.
        int pos = 5 * 4;
        while (pos + 4 <= spirv.Length)
        {
            uint head = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(pos));
            int words = (int)(head >> 16);
            if (words == 0)
            {
                break;
            }
            if ((head & 0xFFFF) == 15)
            {
                int start = pos + 12;
                int end = Array.IndexOf(spirv, (byte)0, start);
                return Encoding.UTF8.GetString(spirv, start, end - start);
            }
            pos += words * 4;
        }
        throw new InvalidOperationException("SPIR-V module has no OpEntryPoint");
    }

    /// <summary>Sorts the 32-bit words of a buffer ascending (for lists whose order depends on atomics).</summary>
    internal static byte[] SortWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        System.Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);
        Array.Sort(words);
        byte[] sorted = new byte[bytes.Length];
        System.Buffer.BlockCopy(words, 0, sorted, 0, sorted.Length);
        return sorted;
    }

    private sealed class RunState : IDisposable
    {
        private readonly VkContext ctx;
        private readonly Vk vk;
        private readonly Device dev;
        private readonly CaseModel model;
        private readonly CaseData data;
        private readonly Func<string, ShaderStage, byte[]> spirv;
        private readonly List<Action> cleanup = new();

        public bool YUp { get; init; }

        /// <summary>Specialization data per entry point name (null: none); built with the library's layout helpers, so the bytes are what a host passes.</summary>
        public Func<string, global::OneShader.Layouts.Vulkan.SpecializationInfo?>? Specialization { get; init; }

        /// <summary>A <c>VkSpecializationInfo</c> for the entry point, freed with the run; null when it has no specialization constants.</summary>
        private SpecializationInfo* SpecInfoFor(string entry)
        {
            var info = Specialization?.Invoke(entry);
            if (info is null || info.MapEntries.Count == 0)
            {
                return null;
            }

            int n = info.MapEntries.Count;
            var entries = (SpecializationMapEntry*)Marshal.AllocHGlobal(n * sizeof(SpecializationMapEntry));
            for (int i = 0; i < n; i++)
            {
                var e = info.MapEntries[i];
                entries[i] = new SpecializationMapEntry { ConstantID = e.ConstantId, Offset = e.Offset, Size = e.Size };
            }

            nint payload = Marshal.AllocHGlobal(Math.Max(1, info.Data.Length));
            Marshal.Copy(info.Data, 0, payload, info.Data.Length);
            var si = (SpecializationInfo*)Marshal.AllocHGlobal(sizeof(SpecializationInfo));
            *si = new SpecializationInfo { MapEntryCount = (uint)n, PMapEntries = entries, DataSize = (nuint)info.Data.Length, PData = (void*)payload };
            cleanup.Add(() =>
            {
                Marshal.FreeHGlobal((nint)entries);
                Marshal.FreeHGlobal(payload);
                Marshal.FreeHGlobal((nint)si);
            });
            return si;
        }
        private readonly Dictionary<int, (VkBuffer Buffer, nint Map, int Size)> buffers = new();
        private readonly Dictionary<int, (VkImage Image, ImageView View, int Layers)> images = new();
        private readonly Dictionary<int, VkSampler> samplers = new();

        public RunState(VkContext ctx, CaseModel model, CaseData data, Func<string, ShaderStage, byte[]> spirv)
        {
            this.ctx = ctx;
            vk = ctx.Api;
            dev = ctx.Device;
            this.model = model;
            this.data = data;
            this.spirv = spirv;
        }

        public void Dispose()
        {
            vk.DeviceWaitIdle(dev);
            for (int i = cleanup.Count - 1; i >= 0; i--)
            {
                cleanup[i]();
            }
        }

        private static void Check(Result r, string what)
        {
            if (r != Result.Success)
            {
                throw new InvalidOperationException($"{what} failed: {r}");
            }
        }

        public List<OutputBlob> Execute()
        {
            CreateResources();
            var (layouts, sets, pipelineLayout) = CreateDescriptors();
            _ = layouts;
            var outputs = new List<OutputBlob>();
            var readbacks = new List<(string Key, VkBuffer Buffer, nint Map, int Size, int W, int H, PixelFormat F, bool Image)>();

            if (model.Graphics is { } g)
            {
                var (target, targetView) = CreateTarget(g);
                var (depthImage, depthView) = g.Depth is null ? (default, default) : CreateDepth(g);
                Submit(cb =>
                {
                    // Draw into the uploaded background.
                    DrawGraphics(cb, g, pipelineLayout, sets, targetView, depthView);
                    // The render pass leaves the attachments in TransferSrcOptimal.
                    int size = g.Width * g.Height * ImageGen.BytesPerTexel(g.TargetFormat);
                    var (buf, map) = CreateHostBuffer(size, BufferUsageFlags.TransferDstBit);
                    var region = ImageCopyRegion(g.Width, g.Height, 1);
                    vk.CmdCopyImageToBuffer(cb, target, ImageLayout.TransferSrcOptimal, buf, 1, &region);
                    readbacks.Add(("target", buf, map, size, g.Width, g.Height, g.TargetFormat, true));
                    if (g.Depth is { Output: true })
                    {
                        int depthSize = g.Width * g.Height * 4;
                        var (dbuf, dmap) = CreateHostBuffer(depthSize, BufferUsageFlags.TransferDstBit);
                        var dregion = ImageCopyRegion(g.Width, g.Height, 1, ImageAspectFlags.DepthBit);
                        vk.CmdCopyImageToBuffer(cb, depthImage, ImageLayout.TransferSrcOptimal, dbuf, 1, &dregion);
                        readbacks.Add(("depth", dbuf, dmap, depthSize, g.Width, g.Height, PixelFormat.R32F, true));
                    }
                    HostBarrier(cb, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit);
                });
            }
            else
            {
                var pipelines = new Dictionary<string, Pipeline>();
                foreach (var d in model.Dispatches)
                {
                    if (!pipelines.ContainsKey(d.Entry))
                    {
                        pipelines[d.Entry] = CreateComputePipeline(d.Entry, pipelineLayout);
                    }
                }
                Submit(cb =>
                {
                    bool first = true;
                    foreach (var d in model.Dispatches)
                    {
                        if (!first)
                        {
                            var mb = new MemoryBarrier
                            {
                                SType = StructureType.MemoryBarrier,
                                SrcAccessMask = AccessFlags.ShaderWriteBit,
                                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                            };
                            vk.CmdPipelineBarrier(cb, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &mb, 0, null, 0, null);
                        }
                        first = false;
                        vk.CmdBindPipeline(cb, PipelineBindPoint.Compute, pipelines[d.Entry]);
                        BindSets(cb, PipelineBindPoint.Compute, pipelineLayout, sets);
                        vk.CmdDispatch(cb, (uint)d.X, (uint)d.Y, (uint)d.Z);
                    }

                    // Outputs: images go to a host buffer through a transfer; buffers are host-visible already.
                    for (int i = 0; i < model.Resources.Count; i++)
                    {
                        var r = model.Resources[i];
                        if (!r.Output)
                        {
                            continue;
                        }
                        if (r.Kind == ResourceKind.StorageImage)
                        {
                            var img = images[i];
                            int layers = Math.Max(r.Layers, 1);
                            ImageBarrier(cb, img.Image, ImageLayout.General, ImageLayout.TransferSrcOptimal,
                                PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit,
                                PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit, layers);
                            int size = r.Width * r.Height * layers * IBytes(r.Format);
                            var (buf, map) = CreateHostBuffer(size, BufferUsageFlags.TransferDstBit);
                            var region = ImageCopyRegion(r.Width, r.Height, layers);
                            vk.CmdCopyImageToBuffer(cb, img.Image, ImageLayout.TransferSrcOptimal, buf, 1, &region);
                            // Layers are reported stacked vertically, so one blob is one tall image.
                            readbacks.Add((r.OutputKey, buf, map, size, r.Width, r.Height * layers, r.Format, true));
                        }
                    }
                    var hb = new MemoryBarrier
                    {
                        SType = StructureType.MemoryBarrier,
                        SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                        DstAccessMask = AccessFlags.HostReadBit,
                    };
                    vk.CmdPipelineBarrier(cb, PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                        PipelineStageFlags.HostBit, 0, 1, &hb, 0, null, 0, null);
                });
                foreach (var kv in pipelines)
                {
                    vk.DestroyPipeline(dev, kv.Value, null);
                }
            }

            // Collect in resource order, target (if any) first.
            foreach (var rb in readbacks)
            {
                outputs.Add(new OutputBlob(rb.Key, CopyOut(rb.Map, rb.Size), rb.W, rb.H, rb.F, rb.Image));
            }
            for (int i = 0; i < model.Resources.Count; i++)
            {
                var r = model.Resources[i];
                if (r.Output && r.Kind == ResourceKind.Storage)
                {
                    var b = buffers[i];
                    byte[] bytes = CopyOut(b.Map, b.Size);
                    if (r.Sorted)
                    {
                        bytes = SortWords(bytes);
                    }
                    outputs.Add(new OutputBlob(r.OutputKey, bytes, 0, 0, PixelFormat.R8, false));
                }
            }
            return outputs;
        }

        private static int IBytes(PixelFormat f) => ImageGen.BytesPerTexel(f);

        private static byte[] CopyOut(nint map, int size)
        {
            byte[] bytes = new byte[size];
            Marshal.Copy(map, bytes, 0, size);
            return bytes;
        }

        private void HostBarrier(CommandBuffer cb, PipelineStageFlags srcStage, AccessFlags srcAccess)
        {
            var hb = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = srcAccess, DstAccessMask = AccessFlags.HostReadBit };
            vk.CmdPipelineBarrier(cb, srcStage, PipelineStageFlags.HostBit, 0, 1, &hb, 0, null, 0, null);
        }

        private static BufferImageCopy ImageCopyRegion(int w, int h, int layers, ImageAspectFlags aspect = ImageAspectFlags.ColorBit, int level = 0, ulong bufferOffset = 0) => new()
        {
            BufferOffset = bufferOffset,
            ImageSubresource = new ImageSubresourceLayers { AspectMask = aspect, MipLevel = (uint)level, LayerCount = (uint)layers },
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };

        // --- one-shot submission -------------------------------------------------------------

        private void Submit(Action<CommandBuffer> record)
        {
            var ai = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = ctx.Pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(vk.AllocateCommandBuffers(dev, &ai, out CommandBuffer cb), "vkAllocateCommandBuffers");
            var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(vk.BeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");
            record(cb);
            Check(vk.EndCommandBuffer(cb), "vkEndCommandBuffer");
            var fi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Check(vk.CreateFence(dev, &fi, null, out Fence fence), "vkCreateFence");
            var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cb };
            Check(vk.QueueSubmit(ctx.Queue, 1, &si, fence), "vkQueueSubmit");
            Check(vk.WaitForFences(dev, 1, &fence, true, 60_000_000_000UL), "vkWaitForFences");
            vk.DestroyFence(dev, fence, null);
            vk.FreeCommandBuffers(dev, ctx.Pool, 1, &cb);
        }

        // --- buffers and images --------------------------------------------------------------

        private (VkBuffer, nint) CreateHostBuffer(int size, BufferUsageFlags usage)
        {
            var bi = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Check(vk.CreateBuffer(dev, &bi, null, out VkBuffer buf), "vkCreateBuffer");
            vk.GetBufferMemoryRequirements(dev, buf, out MemoryRequirements req);
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = ctx.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
            };
            Check(vk.AllocateMemory(dev, &ai, null, out DeviceMemory mem), "vkAllocateMemory");
            Check(vk.BindBufferMemory(dev, buf, mem, 0), "vkBindBufferMemory");
            void* map;
            Check(vk.MapMemory(dev, mem, 0, req.Size, 0, &map), "vkMapMemory");
            new Span<byte>(map, (int)req.Size).Clear();
            cleanup.Add(() =>
            {
                vk.UnmapMemory(dev, mem);
                vk.DestroyBuffer(dev, buf, null);
                vk.FreeMemory(dev, mem, null);
            });
            return (buf, (nint)map);
        }

        private (VkBuffer, nint) CreateFilledBuffer(byte[] bytes, BufferUsageFlags usage)
        {
            var (buf, map) = CreateHostBuffer(bytes.Length, usage);
            Marshal.Copy(bytes, 0, map, bytes.Length);
            return (buf, map);
        }

        private (VkImage, ImageView) CreateImage(Format format, int w, int h, int layers, bool array, ImageUsageFlags usage,
            int levels = 1, bool cube = false, ImageAspectFlags aspect = ImageAspectFlags.ColorBit)
        {
            var ii = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = cube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D((uint)w, (uint)h, 1),
                MipLevels = (uint)levels,
                ArrayLayers = (uint)layers,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(vk.CreateImage(dev, &ii, null, out VkImage image), "vkCreateImage");
            vk.GetImageMemoryRequirements(dev, image, out MemoryRequirements req);
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = ctx.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(vk.AllocateMemory(dev, &ai, null, out DeviceMemory mem), "vkAllocateMemory");
            Check(vk.BindImageMemory(dev, image, mem, 0), "vkBindImageMemory");
            var vi = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = cube ? ImageViewType.TypeCube : array ? ImageViewType.Type2DArray : ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, (uint)levels, 0, (uint)layers),
            };
            Check(vk.CreateImageView(dev, &vi, null, out ImageView view), "vkCreateImageView");
            cleanup.Add(() =>
            {
                vk.DestroyImageView(dev, view, null);
                vk.DestroyImage(dev, image, null);
                vk.FreeMemory(dev, mem, null);
            });
            return (image, view);
        }

        private static Format ToVk(PixelFormat f) => f switch
        {
            PixelFormat.R8 => Format.R8Unorm,
            PixelFormat.Rgba8 => Format.R8G8B8A8Unorm,
            PixelFormat.R32F => Format.R32Sfloat,
            PixelFormat.R32UInt => Format.R32Uint,
            PixelFormat.Rgba16F => Format.R16G16B16A16Sfloat,
            PixelFormat.Depth32F => Format.D32Sfloat,
            _ => Format.R32G32B32A32Sfloat,
        };

        private void ImageBarrier(CommandBuffer cb, VkImage image, ImageLayout from, ImageLayout to,
            PipelineStageFlags srcStage, AccessFlags srcAccess, PipelineStageFlags dstStage, AccessFlags dstAccess, int layers,
            int levels = 1, ImageAspectFlags aspect = ImageAspectFlags.ColorBit)
        {
            var b = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, (uint)levels, 0, (uint)layers),
            };
            vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &b);
        }

        /// <summary>Uploads texel bytes into an image and leaves it in <paramref name="finalLayout"/>.</summary>
        private void Upload(VkImage image, byte[] bytes, int w, int h, int layers, ImageLayout finalLayout,
            int levels = 1, int bytesPerTexel = 4, ImageAspectFlags aspect = ImageAspectFlags.ColorBit)
        {
            var (staging, _) = CreateFilledBuffer(bytes, BufferUsageFlags.TransferSrcBit);
            var regions = new BufferImageCopy[levels];
            ulong offset = 0;
            for (int level = 0; level < levels; level++)
            {
                int lw = Math.Max(w >> level, 1);
                int lh = Math.Max(h >> level, 1);
                regions[level] = ImageCopyRegion(lw, lh, layers, aspect, level, offset);
                offset += (ulong)lw * (ulong)lh * (ulong)layers * (ulong)bytesPerTexel;
            }
            Submit(cb =>
            {
                ImageBarrier(cb, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    PipelineStageFlags.TopOfPipeBit, 0, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, layers, levels, aspect);
                fixed (BufferImageCopy* pr = regions)
                {
                    vk.CmdCopyBufferToImage(cb, staging, image, ImageLayout.TransferDstOptimal, (uint)levels, pr);
                }
                ImageBarrier(cb, image, ImageLayout.TransferDstOptimal, finalLayout,
                    PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit,
                    PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, layers, levels, aspect);
            });
        }

        private void CreateResources()
        {
            for (int i = 0; i < model.Resources.Count; i++)
            {
                var r = model.Resources[i];
                switch (r.Kind)
                {
                    case ResourceKind.Uniform:
                    case ResourceKind.Storage:
                        {
                            byte[] bytes = data.Buffers[i];
                            var usage = r.Kind == ResourceKind.Uniform ? BufferUsageFlags.UniformBufferBit : BufferUsageFlags.StorageBufferBit;
                            var (buf, map) = CreateFilledBuffer(bytes, usage);
                            buffers[i] = (buf, map, bytes.Length);
                            break;
                        }
                    case ResourceKind.Texture:
                    case ResourceKind.StorageImage:
                        {
                            bool storage = r.Kind == ResourceKind.StorageImage;
                            int layers = Math.Max(r.Layers, 1);
                            var usage = storage
                                ? ImageUsageFlags.StorageBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit
                                : ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit;
                            var aspect = r.Format == PixelFormat.Depth32F ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
                            var (img, view) = CreateImage(ToVk(r.Format), r.Width, r.Height, layers, r.Layers > 0, usage, r.MipLevels, r.Cube, aspect);
                            Upload(img, data.Images[i], r.Width, r.Height, layers, storage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal,
                                r.MipLevels, ImageGen.BytesPerTexel(r.Format), aspect);
                            images[i] = (img, view, layers);
                            break;
                        }
                    case ResourceKind.Sampler:
                        {
                            var filter = r.Linear ? Filter.Linear : Filter.Nearest;
                            var mode = r.Repeat ? SamplerAddressMode.Repeat : SamplerAddressMode.ClampToEdge;
                            var si = new SamplerCreateInfo
                            {
                                SType = StructureType.SamplerCreateInfo,
                                MagFilter = filter,
                                MinFilter = filter,
                                MipmapMode = r.MipFilter ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
                                AddressModeU = mode,
                                AddressModeV = mode,
                                AddressModeW = mode,
                                CompareEnable = r.Compare,
                                CompareOp = CompareOp.LessOrEqual,
                                MaxLod = r.MipFilter ? 16f : 0f,
                                BorderColor = BorderColor.FloatTransparentBlack,
                            };
                            Check(vk.CreateSampler(dev, &si, null, out VkSampler s), "vkCreateSampler");
                            samplers[i] = s;
                            cleanup.Add(() => vk.DestroySampler(dev, s, null));
                            break;
                        }
                }
            }
        }

        // --- descriptors ---------------------------------------------------------------------

        private (DescriptorSetLayout[] Layouts, DescriptorSet?[] Sets, PipelineLayout Layout) CreateDescriptors()
        {
            var stages = model.Graphics is not null
                ? ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit
                : ShaderStageFlags.ComputeBit;
            int setCount = model.Resources.Count == 0 ? 0 : model.Resources.Max(r => r.Set) + 1;
            var layouts = new DescriptorSetLayout[setCount];
            var sets = new DescriptorSet?[setCount];
            var typeCounts = new Dictionary<DescriptorType, uint>();
            var variableCounts = new Dictionary<int, uint>();
            var bindlessSets = new List<int>();
            var mutableUnion = new List<DescriptorType>();
            for (int s = 0; s < setCount; s++)
            {
                // Resources sharing a (set, binding) are the elements of one descriptor array.
                var groups = model.Resources.Where(r => r.Set == s).GroupBy(r => r.Binding).OrderBy(g => g.Key).ToArray();
                var bindings = new DescriptorSetLayoutBinding[groups.Length];
                var flags = new DescriptorBindingFlags[groups.Length];
                var mutableLists = new List<DescriptorType[]>();
                var mutableOf = new int[groups.Length];
                for (int b = 0; b < groups.Length; b++)
                {
                    var g = groups[b];
                    bool bindless = model.Bindless.Contains((s, g.Key));
                    var kinds = g.Select(r => TypeOf(r.Kind)).Distinct().ToArray();
                    bool mutable = bindless && kinds.Length > 1;
                    mutableOf[b] = -1;
                    if (mutable)
                    {
                        mutableOf[b] = mutableLists.Count;
                        mutableLists.Add(kinds);
                    }

                    bindings[b] = new DescriptorSetLayoutBinding
                    {
                        Binding = (uint)g.Key,
                        DescriptorType = mutable ? DescriptorType.MutableExt : kinds[0],
                        DescriptorCount = (uint)g.Count(),
                        StageFlags = stages,
                    };
                    if (bindless)
                    {
                        flags[b] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit;
                        if (b == groups.Length - 1)
                        {
                            flags[b] |= DescriptorBindingFlags.VariableDescriptorCountBit;
                            variableCounts[s] = (uint)g.Count();
                        }
                    }
                }

                bool anyBindless = flags.Any(f => f != 0);
                if (anyBindless)
                {
                    bindlessSets.Add(s);
                }

                var typeLists = new MutableDescriptorTypeListEXT[mutableLists.Count];
                var typePins = new List<GCHandle>();
                try
                {
                    for (int m = 0; m < mutableLists.Count; m++)
                    {
                        var handle = GCHandle.Alloc(mutableLists[m], GCHandleType.Pinned);
                        typePins.Add(handle);
                        typeLists[m] = new MutableDescriptorTypeListEXT { DescriptorTypeCount = (uint)mutableLists[m].Length, PDescriptorTypes = (DescriptorType*)handle.AddrOfPinnedObject() };
                    }

                    // One list per binding; bindings that are not mutable get an empty list.
                    var perBinding = new MutableDescriptorTypeListEXT[bindings.Length];
                    for (int b = 0; b < bindings.Length; b++)
                    {
                        perBinding[b] = mutableOf[b] >= 0 ? typeLists[mutableOf[b]] : default;
                    }

                    fixed (DescriptorSetLayoutBinding* pb = bindings)
                    fixed (DescriptorBindingFlags* pf = flags)
                    fixed (MutableDescriptorTypeListEXT* pm = perBinding)
                    {
                        var mutableInfo = new MutableDescriptorTypeCreateInfoEXT
                        {
                            SType = StructureType.MutableDescriptorTypeCreateInfoExt,
                            MutableDescriptorTypeListCount = (uint)perBinding.Length,
                            PMutableDescriptorTypeLists = pm,
                        };
                        var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
                        {
                            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                            BindingCount = (uint)flags.Length,
                            PBindingFlags = pf,
                            PNext = mutableLists.Count > 0 ? &mutableInfo : null,
                        };
                        var li = new DescriptorSetLayoutCreateInfo
                        {
                            SType = StructureType.DescriptorSetLayoutCreateInfo,
                            BindingCount = (uint)bindings.Length,
                            PBindings = pb,
                            Flags = flags.Any(f => (f & DescriptorBindingFlags.UpdateAfterBindBit) != 0) ? DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit : 0,
                            PNext = anyBindless ? &flagsInfo : (mutableLists.Count > 0 ? &mutableInfo : null),
                        };
                        Check(vk.CreateDescriptorSetLayout(dev, &li, null, out layouts[s]), "vkCreateDescriptorSetLayout");
                    }
                }
                finally
                {
                    foreach (var h in typePins)
                    {
                        h.Free();
                    }
                }
                var layoutCopy = layouts[s];
                cleanup.Add(() => vk.DestroyDescriptorSetLayout(dev, layoutCopy, null));
                foreach (var b in bindings)
                {
                    typeCounts[b.DescriptorType] = typeCounts.GetValueOrDefault(b.DescriptorType) + b.DescriptorCount;
                }

                // A mutable binding draws its descriptors from the pool's mutable-descriptor size, which carries the type list.
                foreach (var list in mutableLists)
                {
                    foreach (var t in list)
                    {
                        if (!mutableUnion.Contains(t))
                        {
                            mutableUnion.Add(t);
                        }
                    }
                }
            }

            fixed (DescriptorSetLayout* pl = layouts)
            {
                // One push-constant range over every stage the case runs, as long as the case's data.
                var pushRange = new PushConstantRange { StageFlags = stages, Offset = 0, Size = (uint)(data.Push?.Length ?? 0) };
                var pi = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)setCount,
                    PSetLayouts = pl,
                    PushConstantRangeCount = pushRange.Size > 0 ? 1u : 0u,
                    PPushConstantRanges = pushRange.Size > 0 ? &pushRange : null,
                };
                Check(vk.CreatePipelineLayout(dev, &pi, null, out PipelineLayout pipelineLayout), "vkCreatePipelineLayout");
                cleanup.Add(() => vk.DestroyPipelineLayout(dev, pipelineLayout, null));

                if (setCount > 0)
                {
                    var poolSizes = typeCounts.Select(kv => new DescriptorPoolSize(kv.Key, kv.Value)).ToArray();
                    DescriptorPool pool;
                    var unionTypes = mutableUnion.ToArray();
                    var poolLists = new MutableDescriptorTypeListEXT[poolSizes.Length];
                    fixed (DescriptorType* pu = unionTypes)
                    fixed (MutableDescriptorTypeListEXT* pplists = poolLists)
                    fixed (DescriptorPoolSize* pp = poolSizes)
                    {
                        for (int p = 0; p < poolSizes.Length; p++)
                        {
                            if (poolSizes[p].Type == DescriptorType.MutableExt)
                            {
                                poolLists[p] = new MutableDescriptorTypeListEXT { DescriptorTypeCount = (uint)unionTypes.Length, PDescriptorTypes = pu };
                            }
                        }

                        var poolMutable = new MutableDescriptorTypeCreateInfoEXT
                        {
                            SType = StructureType.MutableDescriptorTypeCreateInfoExt,
                            MutableDescriptorTypeListCount = (uint)poolLists.Length,
                            PMutableDescriptorTypeLists = pplists,
                        };
                        var ci = new DescriptorPoolCreateInfo
                        {
                            SType = StructureType.DescriptorPoolCreateInfo,
                            MaxSets = (uint)setCount,
                            PoolSizeCount = (uint)poolSizes.Length,
                            PPoolSizes = pp,
                            Flags = bindlessSets.Count > 0 ? DescriptorPoolCreateFlags.UpdateAfterBindBit : 0,
                            PNext = unionTypes.Length > 0 ? &poolMutable : null,
                        };
                        Check(vk.CreateDescriptorPool(dev, &ci, null, out pool), "vkCreateDescriptorPool");
                    }
                    cleanup.Add(() => vk.DestroyDescriptorPool(dev, pool, null));
                    var allocated = new DescriptorSet[setCount];
                    var counts = new uint[setCount];
                    for (int s = 0; s < setCount; s++)
                    {
                        counts[s] = variableCounts.GetValueOrDefault(s);
                    }

                    fixed (DescriptorSet* pa = allocated)
                    fixed (uint* pc = counts)
                    {
                        var countInfo = new DescriptorSetVariableDescriptorCountAllocateInfo
                        {
                            SType = StructureType.DescriptorSetVariableDescriptorCountAllocateInfo,
                            DescriptorSetCount = (uint)setCount,
                            PDescriptorCounts = pc,
                        };
                        var ai = new DescriptorSetAllocateInfo
                        {
                            SType = StructureType.DescriptorSetAllocateInfo,
                            DescriptorPool = pool,
                            DescriptorSetCount = (uint)setCount,
                            PSetLayouts = pl,
                            PNext = variableCounts.Count > 0 ? &countInfo : null,
                        };
                        Check(vk.AllocateDescriptorSets(dev, &ai, pa), "vkAllocateDescriptorSets");
                    }
                    for (int s = 0; s < setCount; s++)
                    {
                        sets[s] = allocated[s];
                    }
                    WriteDescriptors(allocated);
                }
                return (layouts, sets, pipelineLayout);
            }
        }

        private static DescriptorType TypeOf(ResourceKind k) => k switch
        {
            ResourceKind.Uniform => DescriptorType.UniformBuffer,
            ResourceKind.Storage => DescriptorType.StorageBuffer,
            ResourceKind.Texture => DescriptorType.SampledImage,
            ResourceKind.Sampler => DescriptorType.Sampler,
            _ => DescriptorType.StorageImage,
        };

        private void WriteDescriptors(DescriptorSet[] sets)
        {
            var bufferInfos = new DescriptorBufferInfo[model.Resources.Count];
            var imageInfos = new DescriptorImageInfo[model.Resources.Count];
            var writes = new WriteDescriptorSet[model.Resources.Count];
            fixed (DescriptorBufferInfo* pb = bufferInfos)
            fixed (DescriptorImageInfo* pi = imageInfos)
            {
                for (int i = 0; i < model.Resources.Count; i++)
                {
                    var r = model.Resources[i];
                    var w = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = sets[r.Set],
                        DstBinding = (uint)r.Binding,
                        DstArrayElement = (uint)r.Element,
                        DescriptorCount = 1,
                        DescriptorType = TypeOf(r.Kind),
                    };
                    switch (r.Kind)
                    {
                        case ResourceKind.Uniform:
                        case ResourceKind.Storage:
                            bufferInfos[i] = new DescriptorBufferInfo(buffers[i].Buffer, 0, (ulong)buffers[i].Size);
                            w.PBufferInfo = pb + i;
                            break;
                        case ResourceKind.Texture:
                            imageInfos[i] = new DescriptorImageInfo { ImageView = images[i].View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
                            w.PImageInfo = pi + i;
                            break;
                        case ResourceKind.StorageImage:
                            imageInfos[i] = new DescriptorImageInfo { ImageView = images[i].View, ImageLayout = ImageLayout.General };
                            w.PImageInfo = pi + i;
                            break;
                        default:
                            imageInfos[i] = new DescriptorImageInfo { Sampler = samplers[i] };
                            w.PImageInfo = pi + i;
                            break;
                    }
                    writes[i] = w;
                }
                fixed (WriteDescriptorSet* pw = writes)
                {
                    vk.UpdateDescriptorSets(dev, (uint)writes.Length, pw, 0, null);
                }
            }
        }

        private void PushConstants(CommandBuffer cb, PipelineLayout layout)
        {
            if (data.Push is not { Length: > 0 } bytes)
            {
                return;
            }

            var stages = model.Graphics is not null ? ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit : ShaderStageFlags.ComputeBit;
            fixed (byte* p = bytes)
            {
                vk.CmdPushConstants(cb, layout, stages, 0, (uint)bytes.Length, p);
            }
        }

        private void BindSets(CommandBuffer cb, PipelineBindPoint point, PipelineLayout layout, DescriptorSet?[] sets)
        {
            // Push constants are part of the pipeline state every draw and dispatch binds with its sets.
            PushConstants(cb, layout);
            for (int s = 0; s < sets.Length; s++)
            {
                if (sets[s] is { } set && model.Resources.Any(r => r.Set == s))
                {
                    vk.CmdBindDescriptorSets(cb, point, layout, (uint)s, 1, &set, 0, null);
                }
            }
        }

        // --- shaders and pipelines -----------------------------------------------------------

        private ShaderModule CreateModule(byte[] code)
        {
            fixed (byte* p = code)
            {
                var mi = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)p,
                };
                Check(vk.CreateShaderModule(dev, &mi, null, out ShaderModule module), "vkCreateShaderModule");
                cleanup.Add(() => vk.DestroyShaderModule(dev, module, null));
                return module;
            }
        }

        private Pipeline CreateComputePipeline(string entry, PipelineLayout layout)
        {
            byte[] code = spirv(entry, ShaderStage.Compute);
            var module = CreateModule(code);
            nint name = Marshal.StringToHGlobalAnsi(EntryPointName(code));
            try
            {
                var ci = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Layout = layout,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = module,
                        PName = (byte*)name,
                        PSpecializationInfo = SpecInfoFor(entry),
                    },
                };
                Check(vk.CreateComputePipelines(dev, default, 1, &ci, null, out Pipeline pipeline), "vkCreateComputePipelines");
                return pipeline;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }

        private (VkImage, ImageView) CreateTarget(GraphicsDecl g)
        {
            var (image, view) = CreateImage(ToVk(g.TargetFormat), g.Width, g.Height, 1, false,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit);
            Upload(image, data.Background!, g.Width, g.Height, 1, ImageLayout.ColorAttachmentOptimal, 1, ImageGen.BytesPerTexel(g.TargetFormat));
            return (image, view);
        }

        private (VkImage, ImageView) CreateDepth(GraphicsDecl g) =>
            CreateImage(Format.D32Sfloat, g.Width, g.Height, 1, false,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransferSrcBit, 1, false, ImageAspectFlags.DepthBit);

        private static Format VertexFormatToVk(VertexFormat f) => f switch
        {
            VertexFormat.Float1 => Format.R32Sfloat,
            VertexFormat.Float2 => Format.R32G32Sfloat,
            VertexFormat.Float3 => Format.R32G32B32Sfloat,
            VertexFormat.Float4 => Format.R32G32B32A32Sfloat,
            VertexFormat.Unorm8x4 => Format.R8G8B8A8Unorm,
            VertexFormat.Uint4 => Format.R32G32B32A32Uint,
            _ => Format.R32Uint,
        };

        private void DrawGraphics(CommandBuffer cb, GraphicsDecl g, PipelineLayout layout, DescriptorSet?[] sets, ImageView targetView, ImageView depthView)
        {
            bool hasDepth = g.Depth is not null;
            // Render pass: one colour attachment that keeps its contents (the uploaded background), plus an
            // optional depth attachment that is cleared and kept for readback.
            var attachments = stackalloc AttachmentDescription[2];
            attachments[0] = new AttachmentDescription
            {
                Format = ToVk(g.TargetFormat),
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.ColorAttachmentOptimal,
                FinalLayout = ImageLayout.TransferSrcOptimal,
            };
            attachments[1] = new AttachmentDescription
            {
                Format = Format.D32Sfloat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.TransferSrcOptimal,
            };
            var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
            var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorRef,
                PDepthStencilAttachment = hasDepth ? &depthRef : null,
            };
            const PipelineStageFlags AttachmentStages = PipelineStageFlags.ColorAttachmentOutputBit
                | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
            var deps = stackalloc SubpassDependency[]
            {
                new SubpassDependency
                {
                    SrcSubpass = Vk.SubpassExternal,
                    DstSubpass = 0,
                    SrcStageMask = AttachmentStages,
                    DstStageMask = AttachmentStages,
                    DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
                        | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
                },
                new SubpassDependency
                {
                    SrcSubpass = 0,
                    DstSubpass = Vk.SubpassExternal,
                    SrcStageMask = AttachmentStages,
                    DstStageMask = PipelineStageFlags.TransferBit,
                    SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                },
            };
            var rpi = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = hasDepth ? 2u : 1u,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 2,
                PDependencies = deps,
            };
            Check(vk.CreateRenderPass(dev, &rpi, null, out RenderPass renderPass), "vkCreateRenderPass");
            cleanup.Add(() => vk.DestroyRenderPass(dev, renderPass, null));
            var views = stackalloc ImageView[] { targetView, depthView };
            var fbi = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = hasDepth ? 2u : 1u,
                PAttachments = views,
                Width = (uint)g.Width,
                Height = (uint)g.Height,
                Layers = 1,
            };
            Check(vk.CreateFramebuffer(dev, &fbi, null, out Framebuffer framebuffer), "vkCreateFramebuffer");
            cleanup.Add(() => vk.DestroyFramebuffer(dev, framebuffer, null));

            // Shaders.
            byte[] vsCode = spirv(g.VertexEntry, ShaderStage.Vertex);
            bool hasPixel = g.PixelEntry.Length > 0;
            byte[] psCode = hasPixel ? spirv(g.PixelEntry, ShaderStage.Pixel) : [];
            var vsModule = CreateModule(vsCode);
            var psModule = hasPixel ? CreateModule(psCode) : default;
            nint vsName = Marshal.StringToHGlobalAnsi(EntryPointName(vsCode));
            nint psName = hasPixel ? Marshal.StringToHGlobalAnsi(EntryPointName(psCode)) : 0;
            var stages = stackalloc PipelineShaderStageCreateInfo[]
            {
                new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsModule, PName = (byte*)vsName, PSpecializationInfo = SpecInfoFor(g.VertexEntry) },
                new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = psModule, PName = (byte*)psName, PSpecializationInfo = hasPixel ? SpecInfoFor(g.PixelEntry) : null },
            };

            // Fixed-function state.
            var bindingDescs = g.VertexBuffers.Select(v => new VertexInputBindingDescription((uint)v.Binding, (uint)v.Stride, v.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex)).ToArray();
            var attrDescs = g.Attributes.Select(a => new VertexInputAttributeDescription((uint)a.Location, (uint)a.Binding, VertexFormatToVk(a.Format), (uint)a.Offset)).ToArray();
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = g.Blend != BlendMode.None,
                SrcColorBlendFactor = g.Blend == BlendMode.Alpha ? BlendFactor.SrcAlpha : BlendFactor.One,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
            };
            var viewport = YUp ? new Viewport(0, g.Height, g.Width, -g.Height, 0, 1) : new Viewport(0, 0, g.Width, g.Height, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)g.Width, (uint)g.Height));
            Pipeline pipeline;
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
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var raster = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = g.Cull switch { CullMode.Back => CullModeFlags.BackBit, CullMode.Front => CullModeFlags.FrontBit, _ => CullModeFlags.None },
                    FrontFace = YUp ? FrontFace.Clockwise : FrontFace.CounterClockwise,
                    LineWidth = 1f,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var blend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment,
                };
                var depthState = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = g.Depth is { Test: true },
                    DepthWriteEnable = hasDepth,
                    DepthCompareOp = CompareOp.Less,
                    MaxDepthBounds = 1f,
                };
                var gci = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = hasPixel ? 2u : 1u,
                    PDepthStencilState = hasDepth ? &depthState : null,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &raster,
                    PMultisampleState = &multisample,
                    PColorBlendState = &blend,
                    Layout = layout,
                    RenderPass = renderPass,
                    Subpass = 0,
                };
                Check(vk.CreateGraphicsPipelines(dev, default, 1, &gci, null, out pipeline), "vkCreateGraphicsPipelines");
            }
            Marshal.FreeHGlobal(vsName);
            if (hasPixel)
            {
                Marshal.FreeHGlobal(psName);
            }
            cleanup.Add(() => vk.DestroyPipeline(dev, pipeline, null));

            // Geometry buffers.
            var vertexBuffers = new List<VkBuffer>();
            var offsets = new List<ulong>();
            foreach (var v in g.VertexBuffers.OrderBy(v => v.Binding))
            {
                vertexBuffers.Add(CreateFilledBuffer(PadTo4(data.VertexBuffers[v.Binding]), BufferUsageFlags.VertexBufferBit).Item1);
                offsets.Add(0);
            }
            VkBuffer indexBuffer = default;
            if (data.Indices is { } indices)
            {
                indexBuffer = CreateFilledBuffer(PadTo4(indices), BufferUsageFlags.IndexBufferBit).Item1;
            }

            // Index 0 (the colour attachment) loads and ignores its value; index 1 is the depth clear.
            var clears = stackalloc ClearValue[2];
            clears[1].DepthStencil = new ClearDepthStencilValue(g.Depth?.Clear ?? 1f, 0);
            var rbi = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                RenderArea = scissor,
                ClearValueCount = hasDepth ? 2u : 0u,
                PClearValues = clears,
            };
            vk.CmdBeginRenderPass(cb, &rbi, SubpassContents.Inline);
            vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
            BindSets(cb, PipelineBindPoint.Graphics, layout, sets);
            if (vertexBuffers.Count > 0)
            {
                var vbs = vertexBuffers.ToArray();
                var offs = offsets.ToArray();
                fixed (VkBuffer* pvb = vbs)
                fixed (ulong* po = offs)
                {
                    vk.CmdBindVertexBuffers(cb, 0, (uint)vbs.Length, pvb, po);
                }
            }
            if (g.IndexData is not null)
            {
                vk.CmdBindIndexBuffer(cb, indexBuffer, 0, g.IndexIs16Bit ? IndexType.Uint16 : IndexType.Uint32);
                vk.CmdDrawIndexed(cb, (uint)g.Count, (uint)g.Instances, 0, 0, 0);
            }
            else
            {
                vk.CmdDraw(cb, (uint)g.Count, (uint)g.Instances, 0, 0);
            }
            vk.CmdEndRenderPass(cb);
        }

        private static byte[] PadTo4(byte[] bytes)
        {
            int size = Math.Max((bytes.Length + 3) & ~3, 4);
            if (size == bytes.Length)
            {
                return bytes;
            }
            byte[] padded = new byte[size];
            bytes.CopyTo(padded, 0);
            return padded;
        }
    }
}
