using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace OneShader.ParityTests;

/// <summary>
/// A Vulkan 1.2 instance and device for headless compute and offscreen rendering, with the
/// Khronos validation layer (including synchronization validation) enabled. Every validation
/// warning or error is recorded and handed to the test that caused it.
/// </summary>
public sealed unsafe partial class VkContext : IDisposable
{
    private readonly List<string> messages = new();
    private readonly DebugUtilsMessengerCallbackFunctionEXT callback;
    private ExtDebugUtils? debugUtils;
    private DebugUtilsMessengerEXT messenger;
    private bool disposed;

    private VkContext()
    {
        callback = OnMessage;
    }

    internal Vk Api { get; private set; } = null!;

    internal Instance Instance { get; private set; }

    /// <summary>True when the device has VK_EXT_mutable_descriptor_type (enabled on the device).</summary>
    internal bool MutableDescriptors { get; private set; }

    internal PhysicalDevice Physical { get; private set; }

    internal Device Device { get; private set; }

    internal Queue Queue { get; private set; }

    internal uint QueueFamily { get; private set; }

    internal CommandPool Pool { get; private set; }

    /// <summary>Name of the selected device, e.g. "NVIDIA GeForce RTX 4070".</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>True when the validation layer is active.</summary>
    public bool ValidationEnabled { get; private set; }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);

    /// <summary>
    /// Creates a context, or returns null with <paramref name="reason"/> when there is no usable Vulkan
    /// device. Device choice: ONESHADER_VK_DEVICE (substring of the device name), else the first discrete GPU.
    /// Set ONESHADER_VK_NO_VALIDATION=1 to run without the layer; otherwise a missing layer is an error.
    /// </summary>
    public static VkContext? TryCreate(out string reason)
    {
        var ctx = new VkContext();
        try
        {
            reason = ctx.Initialize();
            if (reason.Length == 0)
            {
                return ctx;
            }
        }
        catch (DllNotFoundException e)
        {
            reason = "no Vulkan loader: " + e.Message;
        }
        catch (System.IO.FileNotFoundException e)
        {
            // The loader binding reports a missing library this way on some platforms (macOS without a Vulkan SDK).
            reason = "no Vulkan loader: " + e.Message;
        }
        ctx.Dispose();
        return null;
    }

    private string Initialize()
    {
        bool wantValidation = Environment.GetEnvironmentVariable("ONESHADER_VK_NO_VALIDATION") != "1";
        if (wantValidation && Tools.ValidationLayerDir is { } layerDir
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_ADD_LAYER_PATH")))
        {
            // The runtime's Environment class does not reach the native environment on Unix.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (SetEnv("VK_ADD_LAYER_PATH", layerDir, 1) != 0)
                {
                    return "could not set VK_ADD_LAYER_PATH";
                }
            }
        }

        Api = Vk.GetApi();

        bool layerPresent = false;
        uint layerCount = 0;
        Api.EnumerateInstanceLayerProperties(ref layerCount, null);
        var layers = new LayerProperties[layerCount];
        fixed (LayerProperties* pl = layers)
        {
            Api.EnumerateInstanceLayerProperties(ref layerCount, pl);
        }
        foreach (var l in layers)
        {
            if (SilkMarshal.PtrToString((nint)l.LayerName) == "VK_LAYER_KHRONOS_validation")
            {
                layerPresent = true;
            }
        }
        if (wantValidation && !layerPresent)
        {
            return "the Khronos validation layer was not found: run tools/fetch-oracles.sh and set ONESHADER_TOOLS " +
                "(or set ONESHADER_VK_NO_VALIDATION=1 to run without it)";
        }

        nint appName = Marshal.StringToHGlobalAnsi("oneshader-parity");
        nint layerName = Marshal.StringToHGlobalAnsi("VK_LAYER_KHRONOS_validation");
        nint debugExt = Marshal.StringToHGlobalAnsi("VK_EXT_debug_utils");
        nint validationExt = Marshal.StringToHGlobalAnsi("VK_EXT_validation_features");
        try
        {
            var app = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                ApiVersion = Vk.Version12,
            };
            var enabled = stackalloc ValidationFeatureEnableEXT[]
            {
                ValidationFeatureEnableEXT.SynchronizationValidationExt,
            };
            var features = new ValidationFeaturesEXT
            {
                SType = StructureType.ValidationFeaturesExt,
                EnabledValidationFeatureCount = 1,
                PEnabledValidationFeatures = enabled,
            };
            byte** extNames = stackalloc byte*[] { (byte*)debugExt, (byte*)validationExt };
            byte** layerNames = stackalloc byte*[] { (byte*)layerName };
            var info = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &app,
            };
            if (wantValidation)
            {
                info.EnabledLayerCount = 1;
                info.PpEnabledLayerNames = layerNames;
                info.EnabledExtensionCount = 2;
                info.PpEnabledExtensionNames = extNames;
                info.PNext = &features;
            }
            var result = Api.CreateInstance(&info, null, out Instance instance);
            if (result != Result.Success)
            {
                return "vkCreateInstance failed: " + result;
            }
            Instance = instance;
            ValidationEnabled = wantValidation;

            if (wantValidation)
            {
                if (!Api.TryGetInstanceExtension(Instance, out ExtDebugUtils? utils) || utils is null)
                {
                    return "VK_EXT_debug_utils is not available";
                }
                debugUtils = utils;
                var mci = new DebugUtilsMessengerCreateInfoEXT
                {
                    SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                    MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                    MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                        | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                    PfnUserCallback = callback,
                };
                utils.CreateDebugUtilsMessenger(Instance, &mci, null, out messenger);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(appName);
            Marshal.FreeHGlobal(layerName);
            Marshal.FreeHGlobal(debugExt);
            Marshal.FreeHGlobal(validationExt);
        }

        return PickDevice();
    }

    private string PickDevice()
    {
        uint count = 0;
        Api.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0)
        {
            return "no Vulkan physical devices";
        }
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pd = devices)
        {
            Api.EnumeratePhysicalDevices(Instance, ref count, pd);
        }

        string? wanted = Environment.GetEnvironmentVariable("ONESHADER_VK_DEVICE");
        int best = -1;
        int bestScore = -1;
        for (int i = 0; i < devices.Length; i++)
        {
            Api.GetPhysicalDeviceProperties(devices[i], out PhysicalDeviceProperties props);
            string name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "";
            int score = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };
            if (!string.IsNullOrEmpty(wanted))
            {
                score = name.Contains(wanted, StringComparison.OrdinalIgnoreCase) ? 100 : -1;
            }
            if (score > bestScore && props.ApiVersion >= Vk.Version12)
            {
                best = i;
                bestScore = score;
            }
        }
        if (best < 0)
        {
            return string.IsNullOrEmpty(wanted) ? "no Vulkan 1.2 device" : $"no Vulkan 1.2 device matches ONESHADER_VK_DEVICE='{wanted}'";
        }

        Physical = devices[best];
        Api.GetPhysicalDeviceProperties(Physical, out PhysicalDeviceProperties chosen);
        DeviceName = SilkMarshal.PtrToString((nint)chosen.DeviceName) ?? "";

        uint familyCount = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(Physical, ref familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pf = families)
        {
            Api.GetPhysicalDeviceQueueFamilyProperties(Physical, ref familyCount, pf);
        }
        int family = -1;
        for (int i = 0; i < families.Length; i++)
        {
            if (families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit) && families[i].QueueFlags.HasFlag(QueueFlags.ComputeBit))
            {
                family = i;
                break;
            }
        }
        if (family < 0)
        {
            return "no graphics+compute queue family";
        }
        QueueFamily = (uint)family;

        float priority = 1f;
        var qci = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        // DXC may use DrawParameters for SV_InstanceID/SV_VertexID; enable it when the device has it.
        // NonUniformResourceIndex needs the 1.2 non-uniform indexing features.
        var v12Query = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features };
        var v11Query = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &v12Query };
        var query = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &v11Query };
        Api.GetPhysicalDeviceFeatures2(Physical, &query);
        var v12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            ShaderSampledImageArrayNonUniformIndexing = v12Query.ShaderSampledImageArrayNonUniformIndexing,
            ShaderStorageBufferArrayNonUniformIndexing = v12Query.ShaderStorageBufferArrayNonUniformIndexing,
            ShaderUniformBufferArrayNonUniformIndexing = v12Query.ShaderUniformBufferArrayNonUniformIndexing,
            ShaderStorageImageArrayNonUniformIndexing = v12Query.ShaderStorageImageArrayNonUniformIndexing,
            // Bindless cases: unbounded arrays, partially bound and update-after-bind bindings with a variable count.
            RuntimeDescriptorArray = v12Query.RuntimeDescriptorArray,
            DescriptorBindingPartiallyBound = v12Query.DescriptorBindingPartiallyBound,
            DescriptorBindingVariableDescriptorCount = v12Query.DescriptorBindingVariableDescriptorCount,
            DescriptorBindingSampledImageUpdateAfterBind = v12Query.DescriptorBindingSampledImageUpdateAfterBind,
            DescriptorBindingStorageImageUpdateAfterBind = v12Query.DescriptorBindingStorageImageUpdateAfterBind,
            DescriptorBindingStorageBufferUpdateAfterBind = v12Query.DescriptorBindingStorageBufferUpdateAfterBind,
            DescriptorBindingUniformBufferUpdateAfterBind = v12Query.DescriptorBindingUniformBufferUpdateAfterBind,
            DescriptorIndexing = v12Query.DescriptorIndexing,
            // Sized-type cases: 16-bit arithmetic and 64-bit atomics, enabled whenever the device has them.
            ShaderFloat16 = v12Query.ShaderFloat16,
            ShaderBufferInt64Atomics = v12Query.ShaderBufferInt64Atomics,
            ShaderSharedInt64Atomics = v12Query.ShaderSharedInt64Atomics,
        };
        // Descriptor heaps read as several kinds of resource share one binding: that needs VK_EXT_mutable_descriptor_type.
        uint extCount = 0;
        Api.EnumerateDeviceExtensionProperties(Physical, (byte*)null, ref extCount, null);
        var exts = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* pe = exts)
        {
            Api.EnumerateDeviceExtensionProperties(Physical, (byte*)null, ref extCount, pe);
        }

        foreach (var e in exts)
        {
            if (SilkMarshal.PtrToString((nint)e.ExtensionName) == "VK_EXT_mutable_descriptor_type")
            {
                MutableDescriptors = true;
            }
        }

        var mutableFeatures = new PhysicalDeviceMutableDescriptorTypeFeaturesEXT { SType = StructureType.PhysicalDeviceMutableDescriptorTypeFeaturesExt, MutableDescriptorType = true };
        if (MutableDescriptors)
        {
            v12.PNext = &mutableFeatures;
        }

        var v11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = &v12,
            ShaderDrawParameters = v11Query.ShaderDrawParameters,
            StorageBuffer16BitAccess = v11Query.StorageBuffer16BitAccess,
            UniformAndStorageBuffer16BitAccess = v11Query.UniformAndStorageBuffer16BitAccess,
            StoragePushConstant16 = v11Query.StoragePushConstant16,
            StorageInputOutput16 = v11Query.StorageInputOutput16,
        };
        // Descriptor arrays indexed at run time need the dynamic-indexing features; shaders that use SV_ClipDistance /
        // SV_CullDistance declare capabilities that need the clip and cull features.
        var features = new PhysicalDeviceFeatures
        {
            ShaderClipDistance = query.Features.ShaderClipDistance,
            ShaderCullDistance = query.Features.ShaderCullDistance,
            ShaderSampledImageArrayDynamicIndexing = query.Features.ShaderSampledImageArrayDynamicIndexing,
            ShaderStorageBufferArrayDynamicIndexing = query.Features.ShaderStorageBufferArrayDynamicIndexing,
            ShaderUniformBufferArrayDynamicIndexing = query.Features.ShaderUniformBufferArrayDynamicIndexing,
            ShaderStorageImageArrayDynamicIndexing = query.Features.ShaderStorageImageArrayDynamicIndexing,
            ShaderInt16 = query.Features.ShaderInt16,
            ShaderInt64 = query.Features.ShaderInt64,
            ShaderFloat64 = query.Features.ShaderFloat64,
        };
        nint mutableName = SilkMarshal.StringToPtr("VK_EXT_mutable_descriptor_type");
        byte** deviceExts = stackalloc byte*[] { (byte*)mutableName };
        var dci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &v11,
            EnabledExtensionCount = MutableDescriptors ? 1u : 0u,
            PpEnabledExtensionNames = MutableDescriptors ? deviceExts : null,
            PEnabledFeatures = &features,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &qci,
        };
        var result = Api.CreateDevice(Physical, &dci, null, out Device device);
        SilkMarshal.Free(mutableName);
        if (result != Result.Success)
        {
            return "vkCreateDevice failed: " + result;
        }
        Device = device;
        Api.GetDeviceQueue(Device, QueueFamily, 0, out Queue queue);
        Queue = queue;

        var pci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = QueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Api.CreateCommandPool(Device, &pci, null, out CommandPool pool);
        Pool = pool;
        return "";
    }

    private uint OnMessage(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        string text = SilkMarshal.PtrToString((nint)data->PMessage) ?? "(no message)";
        lock (messages)
        {
            messages.Add($"[{severity}] {text}");
        }
        return 0;
    }

    /// <summary>Returns and clears the validation messages recorded since the last call.</summary>
    public IReadOnlyList<string> DrainMessages()
    {
        lock (messages)
        {
            var copy = messages.ToArray();
            messages.Clear();
            return copy;
        }
    }

    internal uint FindMemoryType(uint typeBits, MemoryPropertyFlags wanted)
    {
        Api.GetPhysicalDeviceMemoryProperties(Physical, out PhysicalDeviceMemoryProperties mp);
        for (uint i = 0; i < mp.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 && mp.MemoryTypes[(int)i].PropertyFlags.HasFlag(wanted))
            {
                return i;
            }
        }
        throw new InvalidOperationException($"no Vulkan memory type with {wanted}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (Device.Handle != 0)
        {
            Api.DeviceWaitIdle(Device);
            if (Pool.Handle != 0)
            {
                Api.DestroyCommandPool(Device, Pool, null);
            }
            Api.DestroyDevice(Device, null);
        }
        if (Instance.Handle != 0)
        {
            if (messenger.Handle != 0)
            {
                debugUtils?.DestroyDebugUtilsMessenger(Instance, messenger, null);
            }
            Api.DestroyInstance(Instance, null);
        }
        debugUtils?.Dispose();
        Api?.Dispose();
    }
}
