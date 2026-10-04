using System.Diagnostics;
using System.Text.Json;

namespace Recorder.Core;

public static class ProjectPaths
{
    public static string Root { get; } = FindRoot(AppContext.BaseDirectory);
    public static string Config => Path.Combine(Root, "config", "recorder.json");
    public const string PipeName = "SherpaRecorder.WinUI.v1";
    public const string CaptureLock = @"Local\SherpaRecorder.Capture.v1";
    public static string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
    public static Settings Load() => File.Exists(Config) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Config)) ?? new() : new();
    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        string temporary = Config + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Config, true);
    }
    public static bool OtherRecorderActive()
    {
        string otherRecorder = Path.GetFullPath(Path.Combine(Root, "..", "sherpa-recorder"));
        var paths = new List<string> { Path.Combine(otherRecorder, "run", "recorder.pid") };
        string builds = Path.Combine(otherRecorder, "builds");
        if (Directory.Exists(builds)) paths.AddRange(Directory.GetDirectories(builds).Select(directory => Path.Combine(directory, "run", "recorder.pid")));
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path) || !int.TryParse(File.ReadAllText(path).Trim(), out int id)) continue;
                using Process process = Process.GetProcessById(id);
                if (!process.HasExited && process.ProcessName == "SherpaRecorder") return true;
            }
            catch (ArgumentException) { }
            catch (IOException) { }
        }
        return false;
    }
    private static string FindRoot(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "recorder.project.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("未找到 recorder.project.json，请保持构建目录位于项目内。");
    }
}
