using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Recorder.Core;
using SherpaOnnx;

namespace Recorder.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Directory.SetCurrentDirectory(ProjectPaths.Root);
        using var instance = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\SherpaRecorder.WinUI.Worker.v1", out bool created);
        if (!created) return 3;
        using var server = new RecorderServer();
        if (args.Length >= 2 && args[0] == "--test-wav")
        {
            server.PrintMessages = true;
            await server.Start(ProjectPaths.Load(), args[1], args.Contains("--realtime"));
            return server.State == "Error" ? 1 : 0;
        }
        await server.Listen();
        return 0;
    }
}

internal sealed class RecorderServer : IDisposable
{
    private readonly object gate = new();
    private readonly List<SavedSentence> sentences = new();
    private readonly HashSet<Channel<Message>> clients = new();
    private readonly CancellationTokenSource shutdown = new();
    private string session = "", state = "Idle", partial = "", path = "", pending = "", error = "";
    private double audioSeconds;
    private long sequence;
    private IAudioSource? source;
    private Task? sessionTask;
    private Task? preparation;
    private readonly SemaphoreSlim modelGate = new(1);
    private OnlineRecognizer? cachedRecognizer;
    private string modelKey = "";
    private int modelLoads;
    private bool stopRequested;
    private bool quit;
    public bool PrintMessages { get; set; }
    public string State { get { lock (gate) return state; } }

    private Message Snapshot(string type = "Snapshot", float level = 0) => new(type, session, ++sequence, state, error.Length > 0 ? error : partial, path, audioSeconds, level,
        type == "Snapshot" ? sentences.ToArray() : type == "Final" && sentences.Count > 0 ? [sentences[^1]] : null, pending, ModelReady: cachedRecognizer is not null, ModelLoads: modelLoads);
    private void Send(string type, float level = 0)
    {
        lock (gate)
        {
            Message message = Snapshot(type, level);
            if (PrintMessages) Console.WriteLine(JsonSerializer.Serialize(message));
            foreach (var client in clients)
                if (!(type is "Partial" or "Level") || client.Reader.Count < 8) client.Writer.TryWrite(message);
        }
    }
    public async Task Listen()
    {
        lock (gate) state = "Preparing";
        preparation = Task.Run(() =>
        {
            try
            {
                GetRecognizer(ProjectPaths.Load());
                lock (gate) { if (state == "Preparing") state = "Idle"; }
                Send("ModelReady");
            }
            catch (Exception ex)
            {
                lock (gate) { if (state == "Preparing") { state = "Error"; error = $"模型加载失败：{ex.Message}"; } }
                Send("Error");
            }
        });
        var connections = new List<Task>();
        while (!quit)
        {
            var pipe = new NamedPipeServerStream(ProjectPaths.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(shutdown.Token); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            connections.Add(HandleConnection(pipe));
            connections.RemoveAll(task => task.IsCompletedSuccessfully);
        }
        await Task.WhenAll(connections);
    }
    private async Task HandleConnection(NamedPipeServerStream connectedPipe)
    {
        await using var pipe = connectedPipe;
            using var shutdownRegistration = shutdown.Token.Register(() => pipe.Dispose());
            var queue = Channel.CreateUnbounded<Message>();
            lock (gate) { clients.Add(queue); queue.Writer.TryWrite(Snapshot()); }
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            Task output = Task.Run(async () =>
            {
                try { await foreach (Message message in queue.Reader.ReadAllAsync(connection.Token)) await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), connection.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
            try
            {
                while (pipe.IsConnected && await reader.ReadLineAsync(shutdown.Token) is { } line)
                {
                    Command? command = JsonSerializer.Deserialize<Command>(line);
                    if (command is null || command.Version != 1) continue;
                    switch (command.Type)
                    {
                        case "Start":
                            lock (gate)
                            {
                                if (pending.Length > 0) { error = "尚有未确认保存的文字，请先另存后再开始。"; Send("Error"); }
                                else if (sessionTask is null || sessionTask.IsCompleted) { stopRequested = false; sessionTask = Start(command.Settings ?? new()); }
                                else Send("Snapshot");
                            }
                            break;
                        case "Stop":
                            lock (gate) { stopRequested = true; if (state is "Loading" or "Recording") state = "Stopping"; source?.Stop(); }
                            Send("State");
                            break;
                        case "Snapshot": Send("Snapshot"); break;
                        case "Recover": if (command.Path is not null) Recover(command.Path); break;
                        case "Quit":
                            if (pending.Length > 0) { Send("Error"); break; }
                            lock (gate) { stopRequested = true; source?.Stop(); }
                            if (sessionTask is not null) await sessionTask;
                            if (preparation is not null) await preparation;
                            if (pending.Length > 0) { Send("Error"); break; }
                            quit = true;
                            Send("Quit");
                            break;
                    }
                    if (quit) break;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                lock (gate) clients.Remove(queue);
                queue.Writer.TryComplete();
                try { await output.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
                connection.Cancel();
                if (quit) shutdown.Cancel();
                await output;
                try { writer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
    }

    public Task Start(Settings settings, string? wave = null, bool realtime = false) => Task.Factory.StartNew(() => Run(settings, wave, realtime), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(Settings settings, string? wave, bool realtime)
    {
        using var captureMutex = new Mutex(false, ProjectPaths.CaptureLock);
        bool owned = false;
        StreamWriter? transcript = null;
        StreamWriter? log = null;
        var clock = Stopwatch.StartNew();
        using Process process = Process.GetCurrentProcess();
        TimeSpan initialCpu = process.TotalProcessorTime;
        long samples = 0, lastChanged = 0, nextSentence = 0, latencyTotal = 0, latencyMax = 0;
        int partialUpdates = 0;
        string lastPartial = "";
        OnlineRecognizer? recognizer = null;
        OnlineStream? stream = null;
        try
        {
            try { owned = captureMutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned || ProjectPaths.LegacyRecordingActive()) throw new InvalidOperationException("已有版本正在录音，请结束该次录音后再开始。原进程未被操作。");
            lock (gate) { session = Guid.NewGuid().ToString("N"); state = "Loading"; partial = error = pending = path = ""; audioSeconds = 0; sentences.Clear(); }
            Send("State");
            Directory.CreateDirectory(ProjectPaths.Resolve("logs"));
            log = new StreamWriter(ProjectPaths.Resolve($"logs/{DateTime.Now:yyyyMMdd-HHmmss}-{session[..8]}.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            recognizer = GetRecognizer(settings); stream = recognizer.CreateStream();
            _ = recognizer.GetResult(stream).Text;
            string directory = ProjectPaths.Resolve(settings.RecordingDirectory);
            Directory.CreateDirectory(directory);
            string safe = string.Concat(settings.CourseName.Take(100).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            lock (gate) path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}-{session[..8]}.txt");
            transcript = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            transcript.WriteLine($"课程名称：{settings.CourseName}\n开始时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n");
            Flush(transcript);
            IAudioSource audio = wave is null ? new NativeAudioCapture(settings.DeviceId) : new WaveSource(wave, realtime);
            lock (gate) source = audio;
            audio.StartAsync().GetAwaiter().GetResult();
            lock (gate) { if (stopRequested) { state = "Stopping"; audio.Stop(); } else state = "Recording"; }
            Send("Ready");
            log.WriteLine(wave is null ? "WASAPI shared / native high-quality conversion / float32 mono 16000 Hz" : $"WAV test source / 16000 Hz mono / realtime={realtime} / {wave}");
            long lastLevel = 0;
            while (audio.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (audio.Reader.TryRead(out AudioChunk? chunk))
                {
                    samples += chunk.Samples.Length;
                    lock (gate) audioSeconds = samples / 16000.0;
                    stream.AcceptWaveform(16000, chunk.Samples);
                    Decode();
                    string text = recognizer.GetResult(stream).Text.Trim();
                    if (text.Length > 0 && text != lastPartial)
                    {
                        lastPartial = text; lastChanged = samples;
                        long latency = Math.Max(0, Environment.TickCount64 - chunk.CapturedAt);
                        latencyTotal += latency; latencyMax = Math.Max(latencyMax, latency); partialUpdates++;
                        lock (gate) partial = text;
                        Send("Partial");
                    }
                    if (Environment.TickCount64 - lastLevel >= 150)
                    {
                        lastLevel = Environment.TickCount64;
                        float peak = 0; foreach (float value in chunk.Samples) peak = Math.Max(peak, Math.Abs(value));
                        Send("Level", Math.Min(1, peak));
                    }
                    if (recognizer.IsEndpoint(stream) && (text.Length == 0 || samples - lastChanged >= 16000 * Math.Clamp(settings.StabilitySeconds, 0.5, 5)))
                    {
                        Commit(); recognizer.Reset(stream); lastPartial = ""; lastChanged = samples;
                    }
                }
            }
            stream.AcceptWaveform(16000, new float[12800]); stream.InputFinished(); Decode(); Commit();
            Flush(transcript); transcript.Dispose(); transcript = null;
            lock (gate) { state = "Stopped"; partial = ""; }
            clock.Stop(); process.Refresh();
            double cpu = (process.TotalProcessorTime - initialCpu).TotalMilliseconds / Math.Max(1, clock.Elapsed.TotalMilliseconds) / Environment.ProcessorCount * 100;
            log.WriteLine($"正常停止; audio={samples / 16000.0:F2}s; runtime={clock.Elapsed}; saved={nextSentence}; CPU={cpu:F1}%; peakMemory={process.PeakWorkingSet64 / 1048576.0:F1}MB; partialLatencyAvg={latencyTotal / Math.Max(1, partialUpdates)}ms; max={latencyMax}ms");
            Send("Stopped");

            void Decode() { while (recognizer.IsReady(stream)) recognizer.Decode(stream); }
            void Commit()
            {
                string text = recognizer.GetResult(stream).Text.Trim();
                if (text.Length == 0) return;
                lock (gate) pending = text;
                string timestamp = Timestamp(samples / 16000.0);
                transcript!.WriteLine($"[{timestamp}] {text}");
                Flush(transcript);
                lock (gate) { sentences.Add(new(++nextSentence, timestamp, text)); pending = partial = ""; }
                log.WriteLine($"Saved {nextSentence}: {text}");
                Send("Final");
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                state = "Error"; error = ex.Message;
                if (pending.Length == 0 && stream is not null && recognizer is not null)
                    try { pending = recognizer.GetResult(stream).Text.Trim(); } catch { }
            }
            try { source?.Stop(); } catch { }
            try { log?.WriteLine($"ERROR: {ex}\n尚未确认保存: {pending}"); } catch { }
            Send("Error");
        }
        finally
        {
            IAudioSource? audio;
            lock (gate) { audio = source; source = null; }
            try { audio?.Dispose(); } catch (Exception ex) { log?.WriteLine($"音频释放失败: {ex.Message}"); }
            try { transcript?.Dispose(); } catch { }
            stream?.Dispose(); log?.Dispose();
            if (owned) captureMutex.ReleaseMutex();
        }
    }
    private OnlineRecognizer GetRecognizer(Settings settings)
    {
        modelGate.Wait();
        try
        {
            string model = Path.GetRelativePath(ProjectPaths.Root, ProjectPaths.Resolve(settings.ModelDirectory));
            int threads = Math.Clamp(settings.ModelThreads, 1, 8);
            string key = $"{model}|{threads}";
            if (cachedRecognizer is not null && modelKey == key) return cachedRecognizer;
            if (model.Any(c => c > 127)) throw new InvalidOperationException("模型相对路径必须只包含英文字符，请将模型放入项目 models 目录。");
            foreach (string file in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
                if (!File.Exists(Path.Combine(model, file))) throw new FileNotFoundException("缺少模型文件", Path.Combine(model, file));
            var clock = Stopwatch.StartNew();
            var config = new OnlineRecognizerConfig();
            config.FeatConfig.SampleRate = 16000; config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Transducer.Encoder = Path.Combine(model, "encoder.int8.onnx").Replace('\\', '/');
            config.ModelConfig.Transducer.Decoder = Path.Combine(model, "decoder.int8.onnx").Replace('\\', '/');
            config.ModelConfig.Transducer.Joiner = Path.Combine(model, "joiner.int8.onnx").Replace('\\', '/');
            config.ModelConfig.Tokens = Path.Combine(model, "tokens.txt").Replace('\\', '/');
            config.ModelConfig.Provider = "cpu"; config.ModelConfig.NumThreads = threads;
            config.DecodingMethod = "greedy_search"; config.EnableEndpoint = 1;
            config.Rule1MinTrailingSilence = 2.4f; config.Rule2MinTrailingSilence = 1.2f; config.Rule3MinUtteranceLength = 20;
            var loaded = new OnlineRecognizer(config);
            try
            {
                using var warmup = loaded.CreateStream();
                warmup.AcceptWaveform(16000, new float[19200]);
                while (loaded.IsReady(warmup)) loaded.Decode(warmup);
                _ = loaded.GetResult(warmup).Text;
            }
            catch { loaded.Dispose(); throw; }
            cachedRecognizer?.Dispose();
            lock (gate) { cachedRecognizer = loaded; modelKey = key; modelLoads++; }
            Directory.CreateDirectory(ProjectPaths.Resolve("logs"));
            File.AppendAllText(ProjectPaths.Resolve($"logs/model-{Environment.ProcessId}.log"), $"{DateTimeOffset.Now:O} loaded={modelLoads}; sherpa-onnx=1.13.8; model={model}; threads={threads}; initialization={clock.Elapsed.TotalSeconds:F2}s\n", new UTF8Encoding(false));
            return loaded;
        }
        finally { modelGate.Release(); }
    }
    public void Dispose()
    {
        preparation?.GetAwaiter().GetResult();
        cachedRecognizer?.Dispose();
        modelGate.Dispose();
        shutdown.Dispose();
    }
    private static void Flush(StreamWriter writer) { writer.Flush(); ((FileStream)writer.BaseStream).Flush(true); }
    private static string Timestamp(double seconds) { var t = TimeSpan.FromSeconds(seconds); return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"; }
    private void Recover(string directory)
    {
        try
        {
            string text; lock (gate) { if (state != "Error" || pending.Length == 0 || sessionTask is { IsCompleted: false }) return; text = pending; }
            Directory.CreateDirectory(directory);
            string recovery = Path.Combine(directory, $"recovered-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            using var writer = new StreamWriter(new FileStream(recovery, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            writer.WriteLine($"恢复记录（原文件尾部可能已有未确认写入内容，请核对）\n原记录：{path}\n[{Timestamp(audioSeconds)}] {text}"); Flush(writer);
            lock (gate) { pending = ""; error = $"未确认文本已另存到：{recovery}"; }
            Send("Recovered");
        }
        catch (Exception ex) { lock (gate) error = $"另存失败，结果仍保留：{ex.Message}"; Send("Error"); }
    }
}
