using System.Runtime.InteropServices;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Performs the inexpensive Vulkan checks that can be completed before the
/// native paraLLEl-RDP command processor is created. The native adapter remains
/// authoritative because it also validates the required Vulkan feature bits.
/// </summary>
public static class ParallelRdpSupport
{
    private const uint VkSuccess = 0;
    private const uint VkStructureTypeApplicationInfo = 0;
    private const uint VkStructureTypeInstanceCreateInfo = 1;
    private const int ExtensionNameCapacity = 256;
    private const int ExtensionPropertySize = ExtensionNameCapacity + sizeof(uint);
    private const int PhysicalDeviceNameOffset = sizeof(uint) * 5;
    private const int PhysicalDeviceNameCapacity = 256;
    private const int PhysicalDevicePropertiesCapacity = 4096;

    public const string Storage8BitExtension = "VK_KHR_8bit_storage";
    public const string Storage16BitExtension = "VK_KHR_16bit_storage";
    public const string ExternalMemoryHostExtension = "VK_EXT_external_memory_host";

    /// <summary>
    /// Probes the installed Vulkan loader and physical devices without
    /// creating a logical device or allocating GPU resources.
    /// </summary>
    public static ParallelRdpPreflightResult Probe()
    {
        if (!TryLoadVulkan(out var library))
        {
            return ParallelRdpPreflightResult.LoaderMissing;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, "vkGetInstanceProcAddr", out var export))
            {
                return new ParallelRdpPreflightResult(
                    true,
                    false,
                    [],
                    "The Vulkan loader does not export vkGetInstanceProcAddr.");
            }

            var getInstanceProcAddress =
                Marshal.GetDelegateForFunctionPointer<VkGetInstanceProcAddr>(export);
            var loaderVersion = ReadLoaderVersion(getInstanceProcAddress);
            if (!IsAtLeastVulkan11(loaderVersion))
            {
                return new ParallelRdpPreflightResult(
                    true,
                    false,
                    [],
                    $"Vulkan {FormatVersion(loaderVersion)} is installed; paraLLEl-RDP requires Vulkan 1.1 or newer.");
            }

            var createInstance = GetDelegate<VkCreateInstance>(
                getInstanceProcAddress,
                IntPtr.Zero,
                "vkCreateInstance");
            if (createInstance is null)
            {
                return new ParallelRdpPreflightResult(
                    true,
                    false,
                    [],
                    "The Vulkan loader does not expose vkCreateInstance.");
            }

            var applicationName = Marshal.StringToCoTaskMemUTF8("Pixel64 paraLLEl-RDP preflight");
            var engineName = Marshal.StringToCoTaskMemUTF8("Pixel64");
            var applicationInfoPointer = IntPtr.Zero;
            try
            {
                var applicationInfo = new VkApplicationInfo
                {
                    StructureType = VkStructureTypeApplicationInfo,
                    ApplicationName = applicationName,
                    EngineName = engineName,
                    ApiVersion = MakeVersion(1, 1, 0),
                };
                applicationInfoPointer = Marshal.AllocHGlobal(
                    Marshal.SizeOf<VkApplicationInfo>());
                Marshal.StructureToPtr(applicationInfo, applicationInfoPointer, false);

                var instanceInfo = new VkInstanceCreateInfo
                {
                    StructureType = VkStructureTypeInstanceCreateInfo,
                    ApplicationInfo = applicationInfoPointer,
                };
                if (createInstance(ref instanceInfo, IntPtr.Zero, out var instance) != VkSuccess ||
                    instance == IntPtr.Zero)
                {
                    return new ParallelRdpPreflightResult(
                        true,
                        false,
                        [],
                        "Vulkan 1.1 is installed, but Pixel64 could not create a probe instance.");
                }

                try
                {
                    return ProbePhysicalDevices(getInstanceProcAddress, instance);
                }
                finally
                {
                    GetDelegate<VkDestroyInstance>(
                        getInstanceProcAddress,
                        instance,
                        "vkDestroyInstance")?.Invoke(instance, IntPtr.Zero);
                }
            }
            finally
            {
                if (applicationInfoPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(applicationInfoPointer);
                }

                Marshal.FreeCoTaskMem(applicationName);
                Marshal.FreeCoTaskMem(engineName);
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    /// <summary>
    /// Evaluates enumerated device data. Kept separate from the native probe so
    /// the compatibility policy is deterministic and regression-testable.
    /// </summary>
    public static ParallelRdpPreflightResult Evaluate(
        bool loaderAvailable,
        IReadOnlyList<ParallelRdpVulkanDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (!loaderAvailable)
        {
            return ParallelRdpPreflightResult.LoaderMissing;
        }

        var compatible = devices.Where(IsPreflightCompatible).ToArray();
        if (compatible.Length == 0)
        {
            return new ParallelRdpPreflightResult(
                true,
                false,
                devices,
                "No Vulkan device exposes the API and storage capabilities required by paraLLEl-RDP.");
        }

        var fastPathCount = compatible.Count(
            device => device.Extensions.Contains(
                ExternalMemoryHostExtension,
                StringComparer.Ordinal));
        var pathDescription = fastPathCount > 0
            ? "At least one compatible Vulkan device exposes the zero-copy external-host-memory path."
            : "A compatible Vulkan device was found; paraLLEl-RDP will use its slower RDRAM upload path.";
        return new ParallelRdpPreflightResult(
            true,
            true,
            devices,
            $"{pathDescription} Native feature validation is still required.");
    }

    public static uint MakeVersion(uint major, uint minor, uint patch) =>
        (major << 22) | (minor << 12) | patch;

    public static string FormatVersion(uint version) =>
        $"{(version >> 22) & 0x7F}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";

    private static bool TryLoadVulkan(out IntPtr library)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "vulkan-1.dll" }
            : OperatingSystem.IsLinux()
                ? new[] { "libvulkan.so.1", "libvulkan.so" }
                : Array.Empty<string>();
        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, out library))
            {
                return true;
            }
        }

        library = IntPtr.Zero;
        return false;
    }

    private static uint ReadLoaderVersion(VkGetInstanceProcAddr getInstanceProcAddress)
    {
        var enumerateVersion = GetDelegate<VkEnumerateInstanceVersion>(
            getInstanceProcAddress,
            IntPtr.Zero,
            "vkEnumerateInstanceVersion");
        if (enumerateVersion is null)
        {
            return MakeVersion(1, 0, 0);
        }

        return enumerateVersion(out var version) == VkSuccess
            ? version
            : MakeVersion(1, 0, 0);
    }

    private static ParallelRdpPreflightResult ProbePhysicalDevices(
        VkGetInstanceProcAddr getInstanceProcAddress,
        IntPtr instance)
    {
        var enumerateDevices = GetDelegate<VkEnumeratePhysicalDevices>(
            getInstanceProcAddress,
            instance,
            "vkEnumeratePhysicalDevices");
        var getProperties = GetDelegate<VkGetPhysicalDeviceProperties>(
            getInstanceProcAddress,
            instance,
            "vkGetPhysicalDeviceProperties");
        var enumerateExtensions = GetDelegate<VkEnumerateDeviceExtensionProperties>(
            getInstanceProcAddress,
            instance,
            "vkEnumerateDeviceExtensionProperties");
        if (enumerateDevices is null || getProperties is null || enumerateExtensions is null)
        {
            return new ParallelRdpPreflightResult(
                true,
                false,
                [],
                "The Vulkan instance is missing physical-device enumeration functions.");
        }

        uint deviceCount = 0;
        if (enumerateDevices(instance, ref deviceCount, IntPtr.Zero) != VkSuccess ||
            deviceCount == 0)
        {
            return Evaluate(true, []);
        }

        var devicePointers = Marshal.AllocHGlobal(
            checked((int)deviceCount * IntPtr.Size));
        try
        {
            if (enumerateDevices(instance, ref deviceCount, devicePointers) != VkSuccess)
            {
                return Evaluate(true, []);
            }

            var devices = new List<ParallelRdpVulkanDevice>(checked((int)deviceCount));
            for (var index = 0; index < deviceCount; index++)
            {
                var physicalDevice = Marshal.ReadIntPtr(
                    devicePointers,
                    checked((int)index * IntPtr.Size));
                devices.Add(ReadDevice(physicalDevice, getProperties, enumerateExtensions));
            }

            return Evaluate(true, devices);
        }
        finally
        {
            Marshal.FreeHGlobal(devicePointers);
        }
    }

    private static ParallelRdpVulkanDevice ReadDevice(
        IntPtr physicalDevice,
        VkGetPhysicalDeviceProperties getProperties,
        VkEnumerateDeviceExtensionProperties enumerateExtensions)
    {
        var properties = Marshal.AllocHGlobal(PhysicalDevicePropertiesCapacity);
        try
        {
            getProperties(physicalDevice, properties);
            var apiVersion = unchecked((uint)Marshal.ReadInt32(properties));
            var namePointer = IntPtr.Add(properties, PhysicalDeviceNameOffset);
            var name = Marshal.PtrToStringUTF8(
                           namePointer,
                           NullTerminatedLength(namePointer, PhysicalDeviceNameCapacity))
                       ?? "Unknown Vulkan device";
            return new ParallelRdpVulkanDevice(
                name,
                apiVersion,
                ReadExtensions(physicalDevice, enumerateExtensions));
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    private static string[] ReadExtensions(
        IntPtr physicalDevice,
        VkEnumerateDeviceExtensionProperties enumerateExtensions)
    {
        uint extensionCount = 0;
        if (enumerateExtensions(
                physicalDevice,
                IntPtr.Zero,
                ref extensionCount,
                IntPtr.Zero) != VkSuccess ||
            extensionCount == 0)
        {
            return [];
        }

        var properties = Marshal.AllocHGlobal(
            checked((int)extensionCount * ExtensionPropertySize));
        try
        {
            if (enumerateExtensions(
                    physicalDevice,
                    IntPtr.Zero,
                    ref extensionCount,
                    properties) != VkSuccess)
            {
                return [];
            }

            var extensions = new string[extensionCount];
            for (var index = 0; index < extensionCount; index++)
            {
                var namePointer = IntPtr.Add(
                    properties,
                    checked((int)index * ExtensionPropertySize));
                extensions[index] = Marshal.PtrToStringUTF8(
                                        namePointer,
                                        NullTerminatedLength(
                                            namePointer,
                                            ExtensionNameCapacity))
                                    ?? string.Empty;
            }

            return extensions;
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    private static bool IsPreflightCompatible(ParallelRdpVulkanDevice device)
    {
        if (!IsAtLeastVulkan11(device.ApiVersion))
        {
            return false;
        }

        var storageIsCore = device.ApiVersion >= MakeVersion(1, 2, 0);
        return storageIsCore ||
               (device.Extensions.Contains(Storage8BitExtension, StringComparer.Ordinal) &&
                device.Extensions.Contains(Storage16BitExtension, StringComparer.Ordinal));
    }

    private static bool IsAtLeastVulkan11(uint version) =>
        version >= MakeVersion(1, 1, 0);

    private static int NullTerminatedLength(IntPtr pointer, int capacity)
    {
        for (var index = 0; index < capacity; index++)
        {
            if (Marshal.ReadByte(pointer, index) == 0)
            {
                return index;
            }
        }

        return capacity;
    }

    private static T? GetDelegate<T>(
        VkGetInstanceProcAddr getInstanceProcAddress,
        IntPtr instance,
        string name)
        where T : Delegate
    {
        var pointer = getInstanceProcAddress(instance, name);
        return pointer == IntPtr.Zero
            ? null
            : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public uint StructureType;
        public IntPtr Next;
        public IntPtr ApplicationName;
        public uint ApplicationVersion;
        public IntPtr EngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint StructureType;
        public IntPtr Next;
        public uint Flags;
        public IntPtr ApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr EnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr VkGetInstanceProcAddr(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint VkEnumerateInstanceVersion(out uint version);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint VkCreateInstance(
        ref VkInstanceCreateInfo createInfo,
        IntPtr allocator,
        out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkDestroyInstance(IntPtr instance, IntPtr allocator);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint VkEnumeratePhysicalDevices(
        IntPtr instance,
        ref uint count,
        IntPtr devices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkGetPhysicalDeviceProperties(
        IntPtr physicalDevice,
        IntPtr properties);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint VkEnumerateDeviceExtensionProperties(
        IntPtr physicalDevice,
        IntPtr layerName,
        ref uint count,
        IntPtr properties);
}

public sealed record ParallelRdpVulkanDevice(
    string Name,
    uint ApiVersion,
    IReadOnlyList<string> Extensions);

public sealed record ParallelRdpPreflightResult(
    bool LoaderAvailable,
    bool HasCompatibleDevice,
    IReadOnlyList<ParallelRdpVulkanDevice> Devices,
    string Summary)
{
    internal static ParallelRdpPreflightResult LoaderMissing { get; } =
        new(
            false,
            false,
            [],
            "The Vulkan loader is not installed; Pixel64 will use its software renderer.");
}
