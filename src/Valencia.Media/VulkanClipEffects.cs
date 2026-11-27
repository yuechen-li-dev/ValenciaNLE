using System.Runtime.InteropServices;
using Aurelian.Graphics.Plants;
using Aurelian.Graphics.Vulkan.Commanding;
using Aurelian.Graphics.Vulkan.Commanding.Submit;
using Aurelian.Graphics.Vulkan.Device;
using Aurelian.Graphics.Vulkan.Diagnostics;
using Aurelian.Graphics.Vulkan.Resources.Allocation;
using Aurelian.Graphics.Vulkan.Sync;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using BufferHandle = Silk.NET.Vulkan.Buffer;
using VkResult = Silk.NET.Vulkan.Result;

namespace Valencia.Media;

/// <summary>Worker-owned, bounded dispatch. Media input and output remain RGBA; no presentation or device clock.</summary>
internal sealed unsafe class VulkanClipEffects : IDisposable
{
    private readonly AurelianVulkanPlant plant;
    private readonly Stack<IDisposable> owned = new();
    private readonly RawVulkanMemoryAllocator allocator;
    private readonly VulkanCommandSubmitter submitter;
    private readonly VulkanCommandBufferPool pool;
    private readonly VulkanValidationCapture? validation;
    private readonly Storage?[] buffers = new Storage?[3];
    private DescriptorSetLayout layout;
    private DescriptorPool descriptors;
    private DescriptorSet set;
    private PipelineLayout pipelineLayout;
    private Pipeline pipeline;
    private byte[]? pendingTarget;
    private byte[]? uploadedSource;
    private bool faulted;
    public long SourceUploads { get; private set; }
    public long TargetUploads { get; private set; }
    public long TargetReadbacks { get; private set; }
    public long Dispatches { get; private set; }

    public VulkanClipEffects()
    {
        var shader = ClipEffectShader.Load();
        var initialized = VulkanPlantInitializer.CreatePlant(PlantId.Zero, new VulkanPlantOptions(ApplicationName: "Valencia Clip Effects"));
        plant = initialized.Plant ?? throw new InvalidOperationException(string.Join("; ", initialized.Diagnostics.Select(d => d.Message)));
        Own(plant);
        try
        {
            if (plant.Facts.EnabledValidationLayers.Contains("VK_LAYER_KHRONOS_validation")) validation = Own(new VulkanValidationCapture(plant));
            allocator = Own(new RawVulkanMemoryAllocator(plant));
            var fences = Own(VulkanFenceBundle.Create(plant));
            pool = Own(VulkanCommandBufferPool.Create(plant));
            submitter = Own(new VulkanCommandSubmitter(plant, pool, fences));
            CreatePipeline(shader);
        }
        catch { Dispose(); throw; }
    }

    public string DeviceName => plant.Facts.PhysicalDeviceName;
    public bool ValidationAvailable => validation is not null;
    public int ValidationErrors => validation?.ErrorCount ?? 0;

    public void Composite(ClipEffectFrame pass, byte[] pixels, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (faulted) throw new InvalidOperationException("The failed Vulkan effects device must be retired before another dispatch.");
        var input = EnsureStorage(0, pass.Source.Rgba.Length);
        var target = EnsureStorage(1, pixels.Length);
        var parameters = EnsureStorage(2, checked(pass.ParameterCount * sizeof(int)));
        if (!ReferenceEquals(uploadedSource, pass.Source.Rgba))
        {
            pass.Source.Rgba.AsSpan().CopyTo(input.Span);
            uploadedSource = pass.Source.Rgba;
            SourceUploads++;
        }
        if (!ReferenceEquals(pendingTarget, pixels))
        {
            if (pendingTarget is not null) throw new InvalidOperationException("Flush the active effect target before switching.");
            pixels.AsSpan().CopyTo(target.Span);
            TargetUploads++;
        }
        pass.Pack(MemoryMarshal.Cast<byte, int>(parameters.Span));

        var command = pool.Rent(submitter.LastKnownCompletedFenceValue);
        Require(command.Begin().Success, "begin effects command");
        Barrier(command.CommandBuffer, AccessFlags.HostWriteBit | AccessFlags.ShaderWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.HostBit | PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit);
        plant.Vk.CmdBindPipeline(command.CommandBuffer, PipelineBindPoint.Compute, pipeline);
        var descriptor = set;
        plant.Vk.CmdBindDescriptorSets(command.CommandBuffer, PipelineBindPoint.Compute, pipelineLayout, 0, 1, &descriptor, 0, null);
        plant.Vk.CmdDispatch(command.CommandBuffer, (uint)(pass.Width + 15) / 16, (uint)(pass.Height + 15) / 16, 1);
        Barrier(command.CommandBuffer, AccessFlags.ShaderWriteBit, AccessFlags.HostReadBit,
            PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit);
        Require(command.End().Success, "finish effects command");
        var submitted = submitter.Submit(new(command, WaitForCompletion: true, TimeoutNanoseconds: 5_000_000_000, DebugName: "valencia.clip-effects"));
        faulted = !submitted.Success;
        Require(submitted.Success, "dispatch effects: " + string.Join("; ", submitted.Diagnostics.Select(d => d.Message)));
        cancellation.ThrowIfCancellationRequested();
        pendingTarget = pixels;
        Dispatches++;
    }

    public void Flush()
    {
        if (pendingTarget is not { } pixels) return;
        buffers[1]!.Span[..pixels.Length].CopyTo(pixels);
        pendingTarget = null;
        TargetReadbacks++;
    }

    public void BeginFrame()
    {
        // Every successful dispatch has already joined its fence. An aborted sequence frame
        // may leave a target that was never consumed, which the next frame must not publish.
        pendingTarget = null;
    }

    private Storage EnsureStorage(int index, int length)
    {
        if (buffers[index] is { } existing && existing.Span.Length >= length) return existing;
        if (length is <= 0 or > 128 * 1024 * 1024) throw new NotSupportedException("Clip Effects storage must fit within 128 MiB per buffer.");
        var storage = new Storage(plant, allocator, length, index == 1 ? VulkanMemoryUsage.GpuToCpu : VulkanMemoryUsage.CpuToGpu);
        buffers[index]?.Dispose();
        buffers[index] = storage;
        DescriptorBufferInfo info = new(storage.Handle, 0, (ulong)length);
        WriteDescriptorSet write = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)index,
            DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &info };
        plant.Vk.UpdateDescriptorSets(plant.Device, 1, &write, 0, null);
        return storage;
    }

    private void CreatePipeline(ClipEffectShader shader)
    {
        var vk = plant.Vk;
        var device = plant.Device;
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (uint i = 0; i < 3; i++) bindings[i] = new(i, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = bindings };
        Check(vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out layout));
        var size = new DescriptorPoolSize(DescriptorType.StorageBuffer, 3);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size };
        Check(vk.CreateDescriptorPool(device, in poolInfo, null, out descriptors));
        var activeLayout = layout;
        var allocation = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = descriptors,
            DescriptorSetCount = 1, PSetLayouts = &activeLayout };
        Check(vk.AllocateDescriptorSets(device, in allocation, out set));
        var pipelineInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &activeLayout };
        Check(vk.CreatePipelineLayout(device, in pipelineInfo, null, out pipelineLayout));
        ShaderModule module;
        fixed (byte* code = shader.Spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)shader.Spirv.Length, PCode = (uint*)code };
            Check(vk.CreateShaderModule(device, in moduleInfo, null, out module));
        }
        nint entry = SilkMarshal.StringToPtr(shader.Entry);
        try
        {
            var create = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Layout = pipelineLayout,
                Stage = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = (byte*)entry } };
            Check(vk.CreateComputePipelines(device, default, 1, in create, null, out pipeline));
        }
        finally { SilkMarshal.Free(entry); vk.DestroyShaderModule(device, module, null); }
    }

    private void Barrier(CommandBuffer command, AccessFlags source, AccessFlags destination, PipelineStageFlags from, PipelineStageFlags to)
    {
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = source, DstAccessMask = destination };
        plant.Vk.CmdPipelineBarrier(command, from, to, 0, 1, &barrier, 0, null, 0, null);
    }

    private T Own<T>(T resource) where T : IDisposable { owned.Push(resource); return resource; }
    private static void Check(VkResult result) => Require(result == VkResult.Success, result.ToString());
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Vulkan Clip Effects failed to " + message); }

    public void Dispose()
    {
        if (owned.Count == 0) return;
        // A failed submit may still be in flight. This is worker-side teardown only.
        _ = plant.Vk.DeviceWaitIdle(plant.Device);
        if (pipeline.Handle != 0) plant.Vk.DestroyPipeline(plant.Device, pipeline, null);
        if (pipelineLayout.Handle != 0) plant.Vk.DestroyPipelineLayout(plant.Device, pipelineLayout, null);
        if (descriptors.Handle != 0) plant.Vk.DestroyDescriptorPool(plant.Device, descriptors, null);
        if (layout.Handle != 0) plant.Vk.DestroyDescriptorSetLayout(plant.Device, layout, null);
        foreach (var buffer in buffers) buffer?.Dispose();
        while (owned.TryPop(out var resource)) resource.Dispose();
    }

    private sealed class Storage : IDisposable
    {
        private readonly AurelianVulkanPlant plant;
        private readonly int length;
        private VulkanMemoryAllocation? allocation;
        private void* mapped;
        public readonly BufferHandle Handle;
        public Span<byte> Span => new(mapped, length);

        public Storage(AurelianVulkanPlant plant, RawVulkanMemoryAllocator allocator, int length, VulkanMemoryUsage usage)
        {
            this.plant = plant;
            this.length = length;
            var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = (ulong)length,
                Usage = BufferUsageFlags.StorageBufferBit, SharingMode = SharingMode.Exclusive };
            Check(plant.Vk.CreateBuffer(plant.Device, in info, null, out Handle));
            try
            {
                plant.Vk.GetBufferMemoryRequirements(plant.Device, Handle, out var required);
                var result = allocator.Allocate(new(plant.Context.Id, required.Size, required.MemoryTypeBits, usage, "valencia.clip-effects"));
                allocation = result.Allocation ?? throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(d => d.Message)));
                Check(plant.Vk.BindBufferMemory(plant.Device, Handle, allocation.Memory, allocation.Offset));
                void* pointer;
                Check(plant.Vk.MapMemory(plant.Device, allocation.Memory, allocation.Offset, (ulong)length, 0, &pointer));
                mapped = pointer;
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (mapped != null) plant.Vk.UnmapMemory(plant.Device, allocation!.Memory);
            plant.Vk.DestroyBuffer(plant.Device, Handle, null);
            allocation?.Dispose();
        }
    }
}
