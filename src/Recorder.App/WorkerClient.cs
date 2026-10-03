using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Recorder.Core;

namespace Recorder.App;

internal sealed class WorkerClient : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeLock = new(1);
    private NamedPipeClientStream? pipe;
    private StreamWriter? writer;
    private volatile bool connected;
    public event Action<Message>? Received;
    public event Action<string>? ConnectionChanged;
    public bool Connected => connected;
    public void Connect() => _ = Task.Run(Run);
    private async Task Run()
    {
        bool launched = false;
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                pipe = new NamedPipeClientStream(".", ProjectPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.ConnectAsync(150, lifetime.Token); }
                catch (TimeoutException)
                {
                    if (!launched)
                    {
                        string exe = Path.Combine(AppContext.BaseDirectory, "worker", "Recorder.Worker.exe");
                        if (!File.Exists(exe)) throw new FileNotFoundException("工作进程缺失，请使用完整发布目录。", exe);
                        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = ProjectPaths.Root, UseShellExecute = false, CreateNoWindow = true });
                        launched = true;
                    }
                    await pipe.ConnectAsync(15000, lifetime.Token);
                }
                writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                connected = true;
                ConnectionChanged?.Invoke("已连接");
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                {
                    Message? message = JsonSerializer.Deserialize<Message>(line);
                    if (message is not null && message.Version == 1) Received?.Invoke(message);
                }
                connected = false;
                ConnectionChanged?.Invoke("连接中断，正在重连…");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { connected = false; ConnectionChanged?.Invoke($"连接失败：{ex.Message}"); launched = false; }
            finally { connected = false; writer = null; pipe?.Dispose(); pipe = null; }
            try { await Task.Delay(1000, lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task Send(Command command)
    {
        await writeLock.WaitAsync();
        try { if (writer is null || !Connected) throw new IOException("工作进程未连接。"); await writer.WriteLineAsync(JsonSerializer.Serialize(command)); }
        finally { writeLock.Release(); }
    }
    public void Dispose() { lifetime.Cancel(); pipe?.Dispose(); }
}
