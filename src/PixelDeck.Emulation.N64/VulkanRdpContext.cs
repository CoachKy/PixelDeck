using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Native Vulkan GPU Context and Hardware Device Engine for ParaLLEl-RDP.
/// Creates native VkInstance, VkPhysicalDevice, VkDevice, and coherent VkBuffer hardware memory.
/// Fallbacks seamlessly to host SIMD when native Vulkan hardware context is unavailable.
/// </summary>
public unsafe class VulkanRdpContext : IDisposable
{
    private Vk? _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private bool _isVulkanAvailable;
    private bool _disposed;

    public bool IsVulkanAvailable => _isVulkanAvailable;
    public string DeviceName { get; private set; } = "Hardware SIMD / CPU Fallback";

    public VulkanRdpContext()
    {
        InitializeVulkan();
    }

    private void InitializeVulkan()
    {
        try
        {
            var vkApi = Vk.GetApi();
            if (vkApi is null)
            {
                _isVulkanAvailable = false;
                return;
            }

            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Pixel64 ParaLLEl-RDP"),
                ApplicationVersion = Vk.MakeVersion(1, 0, 0),
                PEngineName = (byte*)Marshal.StringToHGlobalAnsi("PixelDeck Engine"),
                EngineVersion = Vk.MakeVersion(1, 0, 0),
                ApiVersion = Vk.Version11
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledLayerCount = 0,
                EnabledExtensionCount = 0
            };

            Instance instance;
            var result = vkApi.CreateInstance(&createInfo, null, &instance);

            Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
            Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);

            if (result != Result.Success)
            {
                _isVulkanAvailable = false;
                return;
            }

            _instance = instance;

            uint deviceCount = 0;
            vkApi.EnumeratePhysicalDevices(_instance, &deviceCount, null);
            if (deviceCount == 0)
            {
                _isVulkanAvailable = false;
                return;
            }

            var physicalDevices = stackalloc PhysicalDevice[(int)deviceCount];
            vkApi.EnumeratePhysicalDevices(_instance, &deviceCount, physicalDevices);
            _physicalDevice = physicalDevices[0];

            PhysicalDeviceProperties properties;
            vkApi.GetPhysicalDeviceProperties(_physicalDevice, &properties);
            DeviceName = Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName) ?? "Vulkan Hardware GPU";

            var queuePriority = 1.0f;
            var queueCreateInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = 0,
                QueueCount = 1,
                PQueuePriorities = &queuePriority
            };

            var deviceCreateInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo
            };

            Device device;
            result = vkApi.CreateDevice(_physicalDevice, &deviceCreateInfo, null, &device);
            if (result == Result.Success)
            {
                _device = device;
                fixed (Queue* queuePtr = &_graphicsQueue)
                {
                    vkApi.GetDeviceQueue(_device, 0, 0, queuePtr);
                }
                _isVulkanAvailable = true;
                CreateRdramBuffer();
            }
            else
            {
                _isVulkanAvailable = false;
            }
        }
        catch
        {
            _isVulkanAvailable = false;
        }
    }

    public Silk.NET.Vulkan.Buffer RdramBuffer => _rdramBuffer;
    public DeviceMemory RdramMemory => _rdramMemory;
    public void* MappedRdramPointer => _mappedRdramPointer;

    private Silk.NET.Vulkan.Buffer _rdramBuffer;
    private DeviceMemory _rdramMemory;
    private void* _mappedRdramPointer;

    public bool CreateRdramBuffer(ulong sizeInBytes = 8 * 1024 * 1024)
    {
        if (!_isVulkanAvailable) return false;
        var vkApi = Vk.GetApi();
        if (vkApi is null) return false;

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = sizeInBytes,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive
        };

        Silk.NET.Vulkan.Buffer buffer;
        if (vkApi.CreateBuffer(_device, &bufferInfo, null, &buffer) != Result.Success)
        {
            return false;
        }

        _rdramBuffer = buffer;

        MemoryRequirements memRequirements;
        vkApi.GetBufferMemoryRequirements(_device, _rdramBuffer, &memRequirements);

        uint memoryTypeIndex = FindMemoryType(vkApi, memRequirements.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = memoryTypeIndex
        };

        DeviceMemory memory;
        if (vkApi.AllocateMemory(_device, &allocInfo, null, &memory) != Result.Success)
        {
            vkApi.DestroyBuffer(_device, _rdramBuffer, null);
            return false;
        }

        _rdramMemory = memory;
        vkApi.BindBufferMemory(_device, _rdramBuffer, _rdramMemory, 0);

        void* mappedPtr;
        if (vkApi.MapMemory(_device, _rdramMemory, 0, sizeInBytes, 0, &mappedPtr) == Result.Success)
        {
            _mappedRdramPointer = mappedPtr;
        }

        return true;
    }

    private uint FindMemoryType(Vk vkApi, uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProperties;
        vkApi.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memProperties);

        for (var i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1 << i)) != 0 &&
                (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                return (uint)i;
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isVulkanAvailable)
        {
            var vkApi = Vk.GetApi();
            if (vkApi is not null)
            {
                if (_mappedRdramPointer != null)
                {
                    vkApi.UnmapMemory(_device, _rdramMemory);
                    _mappedRdramPointer = null;
                }

                if (_rdramMemory.Handle != 0)
                {
                    vkApi.FreeMemory(_device, _rdramMemory, null);
                }

                if (_rdramBuffer.Handle != 0)
                {
                    vkApi.DestroyBuffer(_device, _rdramBuffer, null);
                }

                if (_device.Handle != 0)
                {
                    vkApi.DestroyDevice(_device, null);
                }

                if (_instance.Handle != 0)
                {
                    vkApi.DestroyInstance(_instance, null);
                }
            }
        }

        GC.SuppressFinalize(this);
    }
}
