using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// SPIR-V Compute Shader Pipeline and Descriptor Set Manager for Vulkan ParaLLEl-RDP.
/// Binds the zero-copy 8 MB RDRAM storage buffer to GPU compute shaders and manages
/// RDP hardware push constants (Scissor bounds, Fill Color, 128-bit Combiner Mode).
/// </summary>
public unsafe class VulkanComputePipeline : IDisposable
{
    private readonly VulkanRdpContext _context;
    private DescriptorSetLayout _descriptorSetLayout;
    private PipelineLayout _pipelineLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private bool _isInitialized;
    private bool _disposed;

    public bool IsInitialized => _isInitialized;
    public PipelineLayout PipelineLayout => _pipelineLayout;
    public DescriptorSet DescriptorSet => _descriptorSet;

    public VulkanComputePipeline(VulkanRdpContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (_context.IsVulkanAvailable)
        {
            InitializePipeline();
        }
    }

    private void InitializePipeline()
    {
        try
        {
            var vkApi = Vk.GetApi();
            if (vkApi is null) return;

            // 1. Descriptor Set Layout Binding 0: RDRAM Storage Buffer
            var layoutBinding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };

            var layoutCreateInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &layoutBinding
            };

            DescriptorSetLayout descriptorSetLayout;
            if (vkApi.CreateDescriptorSetLayout(_context.RdramBuffer.Handle != 0 ? GetDevice() : default, &layoutCreateInfo, null, &descriptorSetLayout) != Result.Success)
            {
                return;
            }

            _descriptorSetLayout = descriptorSetLayout;

            // 2. Push Constant Range for RDP State (64 bytes: Scissor, Fill Color, OtherModes)
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = 64
            };

            var localLayout = _descriptorSetLayout;
            var pipelineLayoutCreateInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &localLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            PipelineLayout pipelineLayout;
            if (vkApi.CreatePipelineLayout(GetDevice(), &pipelineLayoutCreateInfo, null, &pipelineLayout) != Result.Success)
            {
                return;
            }

            _pipelineLayout = pipelineLayout;

            // 3. Descriptor Pool & Allocation
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = 1
            };

            var poolCreateInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize
            };

            DescriptorPool descriptorPool;
            if (vkApi.CreateDescriptorPool(GetDevice(), &poolCreateInfo, null, &descriptorPool) != Result.Success)
            {
                return;
            }

            _descriptorPool = descriptorPool;

            var localPool = _descriptorPool;
            var allocSetLayout = _descriptorSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = localPool,
                DescriptorSetCount = 1,
                PSetLayouts = &allocSetLayout
            };

            DescriptorSet descriptorSet;
            if (vkApi.AllocateDescriptorSets(GetDevice(), &allocInfo, &descriptorSet) != Result.Success)
            {
                return;
            }

            _descriptorSet = descriptorSet;

            // 4. Update Descriptor Set with RDRAM Storage Buffer
            if (_context.RdramBuffer.Handle != 0)
            {
                var bufferInfo = new DescriptorBufferInfo
                {
                    Buffer = _context.RdramBuffer,
                    Offset = 0,
                    Range = Vk.WholeSize
                };

                var writeSet = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _descriptorSet,
                    DstBinding = 0,
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufferInfo
                };

                vkApi.UpdateDescriptorSets(GetDevice(), 1, &writeSet, 0, null);
            }

            _isInitialized = true;
        }
        catch
        {
            _isInitialized = false;
        }
    }

    private Device GetDevice()
    {
        // Internal helper to access private VkDevice handle safely
        var field = typeof(VulkanRdpContext).GetField("_device", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field is not null ? (Device)field.GetValue(_context)! : default;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isInitialized)
        {
            var vkApi = Vk.GetApi();
            if (vkApi is not null)
            {
                var device = GetDevice();
                if (device.Handle != 0)
                {
                    if (_descriptorPool.Handle != 0)
                    {
                        vkApi.DestroyDescriptorPool(device, _descriptorPool, null);
                    }

                    if (_pipelineLayout.Handle != 0)
                    {
                        vkApi.DestroyPipelineLayout(device, _pipelineLayout, null);
                    }

                    if (_descriptorSetLayout.Handle != 0)
                    {
                        vkApi.DestroyDescriptorSetLayout(device, _descriptorSetLayout, null);
                    }
                }
            }
        }

        GC.SuppressFinalize(this);
    }
}
