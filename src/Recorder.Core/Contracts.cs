namespace Recorder.Core;

public sealed class Settings
{
    public string CourseName { get; set; } = "English class";
    public string DeviceId { get; set; } = "";
    public string RecordingDirectory { get; set; } = "recordings";
    public string ModelDirectory { get; set; } = "models/sherpa-onnx-nemotron-speech-streaming-en-0.6b-1120ms-int8-2026-04-25";
    public double StabilitySeconds { get; set; } = 1.8;
    public string Theme { get; set; } = "Default";
    public bool CloseToTray { get; set; }
    public int ModelThreads { get; set; } = 2;
}

public sealed record SavedSentence(long Id, string Timestamp, string Text);
public sealed record Command(string Type, Settings? Settings = null, string? Path = null, int Version = 1);
public sealed record Message(string Type, string SessionId, long Sequence, string State, string Text = "", string FilePath = "", double AudioSeconds = 0, float Level = 0, SavedSentence[]? Sentences = null, string? PendingText = null, int Version = 1, bool ModelReady = false, int ModelLoads = 0);
