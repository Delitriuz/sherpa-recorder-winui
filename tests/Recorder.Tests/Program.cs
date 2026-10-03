using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Recorder.Core;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try { await Run(args); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task Run(string[] args)
    {
        var reports = new List<string>();
        List<AudioDevice> devices = NativeAudioCapture.Devices();
        Require(devices.Count >= 1 && devices[0].Id == "", "原生枚举默认设备");
        Require(devices.Select(device => device.Id).Distinct().Count() == devices.Count, "设备 ID 唯一");
        using (var watcher = new DeviceWatcher()) { reports.Add($"PASS native MMDevice enumeration and notification registration; devices={devices.Count}"); }
        using (var source = new WaveSource(ProjectPaths.Resolve("tests/fixtures/same-sentence-twice.wav")))
        {
            await source.StartAsync(); long samples = 0;
            await foreach (AudioChunk chunk in source.Reader.ReadAllAsync()) samples += chunk.Samples.Length;
            Require(samples > 16000, "WAV 音频读取"); reports.Add($"PASS WAV source; samples={samples}");
        }
        bool legacyActive = ProjectPaths.LegacyRecordingActive();
        if (args.Contains("--capture-probe") && !legacyActive)
        {
            using var capture = new NativeAudioCapture("");
            await capture.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Task timer = Task.Run(async () => { await Task.Delay(3000); capture.Stop(); });
            long received = 0;
            await foreach (AudioChunk chunk in capture.Reader.ReadAllAsync())
            {
                Require(chunk.Samples.All(float.IsFinite), "原生样本必须为有限浮点数"); received += chunk.Samples.Length;
            }
            await timer;
            Require(received >= 32000 && received <= 80000, "原生 16k 单声道采集时长");
            reports.Add($"PASS native WASAPI capture and Windows sample conversion; float32 mono 16000Hz; samples={received}");
        }
        {
            string exe = ProjectPaths.Resolve("builds/winui-v3/worker/Recorder.Worker.exe");
            string records = ProjectPaths.Resolve(ProjectPaths.Load().RecordingDirectory);
            int before = Directory.Exists(records) ? Directory.GetFiles(records, "*.txt").Length : 0;
            var startup = Stopwatch.StartNew();
            using Process process = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = ProjectPaths.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
            Task<string> workerError = process.StandardError.ReadToEndAsync();
            Task<string> workerOutput = process.StandardOutput.ReadToEndAsync();
            await using (var pipe = new NamedPipeClientStream(".", ProjectPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(10000);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                Message initial = await Read(reader, "Snapshot");
                Require(initial.State is "Preparing" or "Idle", "启动加载状态");
                if (!initial.ModelReady) initial = await Read(reader, "ModelReady");
                Require(initial.State == "Idle" && initial.ModelReady && initial.ModelLoads == 1 && initial.FilePath.Length == 0 && initial.AudioSeconds == 0, "自动预加载完成且没有录音");
                Require((Directory.Exists(records) ? Directory.GetFiles(records, "*.txt").Length : 0) == before, "预加载不创建课堂记录");
                reports.Add($"PASS startup preload and inference warmup before Start; model loads=1; no recording file; initialization={startup.Elapsed.TotalSeconds:F2}s");
                var invalid = new Settings { ModelDirectory = "tests/intentionally-missing-model" };
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Command("Start", invalid)));
                Message blocked = await Read(reader, "Error"); Require(blocked.Text.Contains(legacyActive ? "已有版本" : "缺少模型"), "采集前保护或模型失败状态");
                await using (var secondary = new NamedPipeClientStream(".", ProjectPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                {
                    await secondary.ConnectAsync(10000);
                    using var secondReader = new StreamReader(secondary, Encoding.UTF8, false, 4096, true);
                    using var secondWriter = new StreamWriter(secondary, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    await Read(secondReader, "Snapshot");
                    await secondWriter.WriteLineAsync(JsonSerializer.Serialize(new Command("Snapshot")));
                    Message shared = await Read(reader, "Snapshot");
                    Require(shared.State == "Error", "UI 与脚本可同时连接");
                }
                using Process duplicate = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = ProjectPaths.Root, UseShellExecute = false, CreateNoWindow = true })!;
                await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Require(duplicate.ExitCode == 3, "工作进程单实例");
            }
            await using (var pipe = new NamedPipeClientStream(".", ProjectPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(10000);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                Message restored = await Read(reader, "Snapshot"); Require(restored.State == "Error" && restored.Text.Contains(legacyActive ? "已有版本" : "缺少模型"), "重连恢复状态");
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Command("Quit")));
                await Read(reader, "Quit");
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Require(process.ExitCode == 0, $"工作进程正常退出：{await workerError}");
            if (legacyActive) Require(ProjectPaths.LegacyRecordingActive(), "旧版录音保持运行");
            reports.Add("PASS IPC snapshot, concurrent clients, reconnect, single worker, failure reporting and graceful worker exit; model preloaded, capture not started");
        }
        Directory.CreateDirectory(ProjectPaths.Resolve("tests/results"));
        if (args.Contains("--recording-probe") && !legacyActive)
        {
            string exe = ProjectPaths.Resolve("builds/winui-v3/worker/Recorder.Worker.exe");
            using Process worker = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = ProjectPaths.Root, UseShellExecute = false, CreateNoWindow = true })!;
            await using var pipe = new NamedPipeClientStream(".", ProjectPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(10000);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            try
            {
            Message prepared = await Read(reader, "Snapshot");
            if (!prepared.ModelReady) prepared = await Read(reader, "ModelReady");
            Require(prepared.ModelReady && prepared.ModelLoads == 1, "录音前模型已预加载");
            Settings settings = ProjectPaths.Load();
            settings.RecordingDirectory = "recorder.project.json";
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Command("Start", settings)));
            Message failure = await Read(reader, "Error");
            Require(failure.State == "Error", "不可写记录目录必须失败");
            await Task.Delay(300);
            settings.RecordingDirectory = "tests/results/microphone"; settings.CourseName = "Native microphone probe";
            for (int attempt = 1; attempt <= 2; attempt++)
            {
            var startClock = Stopwatch.StartNew();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Command("Start", settings)));
            Message ready = await Read(reader, "Ready");
            Require(ready.ModelReady && ready.ModelLoads == 1 && startClock.Elapsed.TotalSeconds < 3, "开始录音直接复用已预热模型");
            double startSeconds = startClock.Elapsed.TotalSeconds;
            await Task.Delay(3000);
            using Process script = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ProjectPaths.Resolve("stop.ps1")}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
            Task<string> scriptError = script.StandardError.ReadToEndAsync();
            Task<string> scriptOutput = script.StandardOutput.ReadToEndAsync();
            await script.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Require(script.ExitCode == 0, $"停止脚本退出 {script.ExitCode}: {await scriptError}; {await scriptOutput}");
            Message stopped = await Read(reader, "Stopped");
            Require(script.ExitCode == 0 && stopped.AudioSeconds >= 2 && File.Exists(stopped.FilePath), "停止脚本与实际录音保存");
            string saved = File.ReadAllText(stopped.FilePath);
            Require(saved.Contains("Native microphone probe") && saved.Contains("开始时间"), "UTF-8 会话头");
            Require(stopped.ModelReady && stopped.ModelLoads == 1, "停止后保留同一个模型");
            reports.Add($"PASS recording attempt={attempt}; ready in {startSeconds:F3}s; model loads={stopped.ModelLoads}; stop.ps1, UTF-8 flush and tail processing; audio={stopped.AudioSeconds:F2}s; file={stopped.FilePath}");
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new Command("Quit")));
            await Read(reader, "Quit");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(worker.ExitCode == 0, "实际录音工作进程正常退出");
            reports.Add("PASS invalid save directory error and 2 microphone sessions reuse the startup model; graceful exit");
            }
            finally
            {
                if (!worker.HasExited)
                    try { await writer.WriteLineAsync(JsonSerializer.Serialize(new Command("Quit"))); await Read(reader, "Quit"); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (Exception ex) { Console.Error.WriteLine($"测试工作进程尚未确认退出，请保留未写文字：{ex.Message}"); }
            }
        }
        File.WriteAllLines(ProjectPaths.Resolve("tests/results/core-tests.txt"), reports);
        foreach (string report in reports) Console.WriteLine(report);
    }
    private static void Require(bool condition, string name) { if (!condition) throw new Exception(name); }
    private static async Task<Message> Read(StreamReader reader, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(type is "Ready" or "ModelReady" or "Error" or "Quit" ? 60 : 15));
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            Message message = JsonSerializer.Deserialize<Message>(line)!;
            if (message.Type == "Error" && type != "Error") throw new IOException(message.Text);
            if (message.Type == type) return message;
        }
        throw new IOException($"未收到 {type}");
    }
}
