using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// Vulkan Command Buffer Queue and GPU Dispatch Manager for ParaLLEl-RDP.
/// Allocates VkCommandPool and records/submits compute dispatches to the hardware VkQueue
/// whenever DP DMA command buffers are processed.
/// </summary>
public unsafe class VulkanCommandBufferQueue : IDisposable
{
    private readonly VulkanRdpContext _context;
    private readonly VulkanComputePipeline _pipeline;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private bool _isInitialized;
    private bool _disposed;

    public bool IsInitialized => _isInitialized;
    public long GpuDispatchesSubmitted { get; private set; }

    public VulkanCommandBufferQueue(VulkanRdpContext context, VulkanComputePipeline pipeline)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

        if (_context.IsVulkanAvailable && _pipeline.IsInitialized)
        {
            InitializeQueue();
        }
    }

    private void InitializeQueue()
    {
        try
        {
            var vkApi = Vk.GetApi();
            if (vkApi is null) return;

            var poolCreateInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = 0,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit
            };

            CommandPool commandPool;
            if (vkApi.CreateCommandPool(GetDevice(), &poolCreateInfo, null, &commandPool) != Result.Success)
            {
                return;
            }

            _commandPool = commandPool;

            var localPool = _commandPool;
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = localPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            CommandBuffer commandBuffer;
            if (vkApi.AllocateCommandBuffers(GetDevice(), &allocInfo, &commandBuffer) != Result.Success)
            {
                return;
            }

            _commandBuffer = commandBuffer;
            _isInitialized = true;
        }
        catch
        {
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Records and submits a Vulkan compute dispatch to the hardware queue for RDP tile rasterization.
    /// </summary>
    public bool DispatchRdpTileRasterizer(int width, int height, ReadOnlySpan<uint> rdpPushConstants)
    {
        if (!_isInitialized || _commandBuffer.Handle == 0)
        {
            return false;
        }

        try
        {
            var vkApi = Vk.GetApi();
            if (vkApi is null) return false;

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };

            if (vkApi.BeginCommandBuffer(_commandBuffer, &beginInfo) != Result.Success)
            {
                return false;
            }

            // Bind Pipeline & Descriptor Sets
            vkApi.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Compute, default);

            var descriptorSet = _pipeline.DescriptorSet;
            var pipelineLayout = _pipeline.PipelineLayout;

            if (descriptorSet.Handle != 0 && pipelineLayout.Handle != 0)
            {
                vkApi.CmdBindDescriptorSets(
                    _commandBuffer,
                    PipelineBindPoint.Compute,
                    pipelineLayout,
                    0,
                    1,
                    &descriptorSet,
                    0,
                    null);
            }

            // Push Constants for RDP Scissor / Fill State
            if (rdpPushConstants.Length > 0 && pipelineLayout.Handle != 0)
            {
                fixed (uint* pushPtr = rdpPushConstants)
                {
                    vkApi.CmdPushConstants(
                        _commandBuffer,
                        pipelineLayout,
                        ShaderStageFlags.ComputeBit,
                        0,
                        (uint)(rdpPushConstants.Length * sizeof(uint)),
                        pushPtr);
                }
            }

            // Compute Workgroup Dispatch: (width+7)/8 x (height+7)/8 x 1
            uint groupX = (uint)((width + 7) / 8);
            uint groupY = (uint)((height + 7) / 8);
            vkApi.CmdDispatch(_commandBuffer, groupX, groupY, 1);

            if (vkApi.EndCommandBuffer(_commandBuffer) != Result.Success)
            {
                return false;
            }

            // Submit to Graphics Queue
            var localCmdBuf = _commandBuffer;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &localCmdBuf
            };

            var queue = GetQueue();
            if (vkApi.QueueSubmit(queue, 1, &submitInfo, default) == Result.Success)
            {
                vkApi.QueueWaitIdle(queue);
                GpuDispatchesSubmitted++;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private Device GetDevice()
    {
        var field = typeof(VulkanRdpContext).GetField("_device", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field is not null ? (Device)field.GetValue(_context)! : default;
    }

    private Queue GetQueue()
    {
        var field = typeof(VulkanRdpContext).GetField("_graphicsQueue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field is not null ? (Queue)field.GetValue(_context)! : default;
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
                if (device.Handle != 0 && _commandPool.Handle != 0)
                {
                    vkApi.DestroyCommandPool(device, _commandPool, null);
                }
            }
        }

        GC.SuppressFinalize(this);
    }
}
