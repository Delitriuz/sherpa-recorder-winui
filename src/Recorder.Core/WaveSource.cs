using System.Text;
using System.Threading.Channels;
namespace Recorder.Core;

// 仅供可重复验收使用，避免测试时占用麦克风。
public sealed class WaveSource : IAudioSource
{
    private readonly string path;
    private readonly bool realtime;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<AudioChunk> channel = Channel.CreateBounded<AudioChunk>(100);
    private Task? task;
    public ChannelReader<AudioChunk> Reader => channel.Reader;
    public WaveSource(string path, bool realtime = false) { this.path = path; this.realtime = realtime; }
    public Task StartAsync() { task = Task.Run(ReadAsync); return Task.CompletedTask; }
    public void Stop() => stop.Cancel();
    private async Task ReadAsync()
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidDataException("需要 RIFF WAV。");
            reader.ReadUInt32();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("需要 WAV。");
            ushort format = 0, channels = 0, bits = 0;
            uint sampleRate = 0;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string id = Encoding.ASCII.GetString(reader.ReadBytes(4)); uint size = reader.ReadUInt32(); long end = reader.BaseStream.Position + size;
                if (id == "fmt ") { format = reader.ReadUInt16(); channels = reader.ReadUInt16(); sampleRate = reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16(); }
                else if (id == "data")
                {
                    if (sampleRate != 16000 || channels != 1 || !(format == 1 && bits == 16 || format == 3 && bits == 32)) throw new InvalidDataException("测试 WAV 需要 16 kHz 单声道 PCM16 或 float32。");
                    while (reader.BaseStream.Position < end && !stop.IsCancellationRequested)
                    {
                        int count = (int)Math.Min(1600, (end - reader.BaseStream.Position) / (bits / 8));
                        var samples = new float[count];
                        for (int i = 0; i < count; i++) samples[i] = format == 1 ? reader.ReadInt16() / 32768f : reader.ReadSingle();
                        await channel.Writer.WriteAsync(new(samples, Environment.TickCount64), stop.Token);
                        if (realtime) await Task.Delay(100, stop.Token);
                    }
                    break;
                }
                reader.BaseStream.Position = end + (size % 2);
            }
            channel.Writer.TryComplete();
        }
        catch (OperationCanceledException) { channel.Writer.TryComplete(); }
        catch (Exception ex) { channel.Writer.TryComplete(ex); }
    }
    public void Dispose() { stop.Cancel(); task?.GetAwaiter().GetResult(); stop.Dispose(); }
}
