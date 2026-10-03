using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Recorder.Core;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record AudioChunk(float[] Samples, long CapturedAt);

public interface IAudioSource : IDisposable
{
    ChannelReader<AudioChunk> Reader { get; }
    Task StartAsync();
    void Stop();
}

public sealed class NativeAudioCapture : IAudioSource
{
    private readonly string deviceId;
    private readonly Channel<AudioChunk> channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(1000) { SingleReader = true, SingleWriter = true });
    private readonly ManualResetEvent stop = new(false);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? thread;
    public ChannelReader<AudioChunk> Reader => channel.Reader;
    public NativeAudioCapture(string deviceId) => this.deviceId = deviceId;

    public static List<AudioDevice> Devices()
    {
        var result = new List<AudioDevice> { new("", "Windows 默认麦克风") };
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator.EnumAudioEndpoints(1, 1, out devices);
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                devices.Item(i, out IMMDevice device);
                IPropertyStore? store = null;
                try
                {
                    device.GetId(out string id);
                    device.OpenPropertyStore(0, out store);
                    var key = new PropertyKey { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
                    store.GetValue(ref key, out PropVariant value);
                    try { result.Add(new(id, value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? id : id)); }
                    finally { PropVariantClear(ref value); }
                }
                finally { Release(store); Release(device); }
            }
        }
        finally { Release(devices); Release(enumerator); }
        return result;
    }

    public Task StartAsync()
    {
        thread = new Thread(Capture) { IsBackground = true, Name = "WASAPI capture" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return started.Task;
    }
    public void Stop() => stop.Set();
    private void Capture()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr format = IntPtr.Zero;
        bool running = false;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (string.IsNullOrEmpty(deviceId)) enumerator.GetDefaultAudioEndpoint(1, 1, out device);
            else enumerator.GetDevice(deviceId, out device);
            Guid iid = typeof(IAudioClient).GUID;
            device.Activate(ref iid, 23, IntPtr.Zero, out object audio);
            client = (IAudioClient)audio;
            var wave = new WaveFormat { Tag = 3, Channels = 1, SamplesPerSecond = 16000, AverageBytesPerSecond = 64000, BlockAlign = 4, BitsPerSample = 32 };
            format = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormat>());
            Marshal.StructureToPtr(wave, format, false);
            // Windows 共享音频引擎执行混音与高质量重采样，无第三方音频依赖。
            client.Initialize(0, 0x80000000u | 0x08000000u | 0x00040000u, 1_000_000, 0, format, IntPtr.Zero);
            using var dataReady = new AutoResetEvent(false);
            client.SetEventHandle(dataReady.SafeWaitHandle.DangerousGetHandle());
            iid = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref iid, out object service);
            capture = (IAudioCaptureClient)service;
            client.Start();
            running = true;
            started.TrySetResult();
            WaitHandle[] waits = [stop, dataReady];
            bool firstPacket = true;
            while (true)
            {
                int signal = WaitHandle.WaitAny(waits, 2000);
                if (signal == 0) { client.Stop(); running = false; Drain(capture, ref firstPacket); break; }
                Drain(capture, ref firstPacket);
            }
            channel.Writer.TryComplete();
        }
        catch (Exception ex) { started.TrySetException(ex); channel.Writer.TryComplete(ex); }
        finally
        {
            if (running) try { client?.Stop(); } catch { }
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Release(capture); Release(client); Release(device); Release(enumerator);
        }
    }
    private void Drain(IAudioCaptureClient capture, ref bool firstPacket)
    {
        capture.GetNextPacketSize(out uint packet);
        while (packet > 0)
        {
            capture.GetBuffer(out IntPtr buffer, out uint frames, out uint flags, out _, out _);
            try
            {
                if ((flags & 1) != 0 && !firstPacket) throw new IOException("麦克风音频发生不连续，已停止以避免静默漏句。");
                firstPacket = false;
                var samples = new float[frames];
                if ((flags & 2) == 0) Marshal.Copy(buffer, samples, 0, samples.Length);
                if (!channel.Writer.TryWrite(new(samples, Environment.TickCount64))) throw new IOException("识别速度不足，音频队列已满。");
            }
            finally { capture.ReleaseBuffer(frames); }
            capture.GetNextPacketSize(out packet);
        }
    }
    public void Dispose() { stop.Set(); thread?.Join(); stop.Dispose(); }
    internal static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}

[StructLayout(LayoutKind.Sequential, Pack = 2)] internal struct WaveFormat
{
    public ushort Tag, Channels;
    public uint SamplesPerSecond, AverageBytesPerSecond;
    public ushort BlockAlign, BitsPerSample, ExtraSize;
}
[StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid FormatId; public uint Id; }
[StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IMMDeviceCollection { void GetCount(out uint count); void Item(uint index, out IMMDevice device); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IMMDevice
{
    void Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    void OpenPropertyStore(uint access, out IPropertyStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out uint state);
}
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IPropertyStore
{
    void GetCount(out uint count); void GetAt(uint index, out PropertyKey key); void GetValue(ref PropertyKey key, out PropVariant value); void SetValue(ref PropertyKey key, ref PropVariant value); void Commit();
}
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IAudioClient
{
    void Initialize(int mode, uint flags, long duration, long period, IntPtr format, IntPtr session);
    void GetBufferSize(out uint frames); void GetStreamLatency(out long latency); void GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
    void GetMixFormat(out IntPtr format); void GetDevicePeriod(out long defaultPeriod, out long minimum);
    void Start(); void Stop(); void Reset(); void SetEventHandle(IntPtr handle);
    void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
}
[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr buffer, out uint frames, out uint flags, out ulong position, out ulong performanceCounter);
    void ReleaseBuffer(uint frames); void GetNextPacketSize(out uint frames);
}
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)] internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)] public sealed class DeviceWatcher : IDisposable, IMMNotificationClient
{
    private readonly IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
    public event Action? Changed;
    public DeviceWatcher() => enumerator.RegisterEndpointNotificationCallback(this);
    int IMMNotificationClient.OnDeviceStateChanged(string id, uint state) { Changed?.Invoke(); return 0; }
    int IMMNotificationClient.OnDeviceAdded(string id) { Changed?.Invoke(); return 0; }
    int IMMNotificationClient.OnDeviceRemoved(string id) { Changed?.Invoke(); return 0; }
    int IMMNotificationClient.OnDefaultDeviceChanged(int flow, int role, string? id) { Changed?.Invoke(); return 0; }
    int IMMNotificationClient.OnPropertyValueChanged(string id, PropertyKey key) { Changed?.Invoke(); return 0; }
    public void Dispose() { enumerator.UnregisterEndpointNotificationCallback(this); NativeAudioCapture.Release(enumerator); }
}
