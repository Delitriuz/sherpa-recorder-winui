# v1.0.1

本次调整界面提示和使用说明，识别与保存逻辑不变。发布包仍包含应用、运行库和 Nemotron INT8 模型。

- 原生 WinUI 3 界面、实时文字、历史文件、主题设置和系统托盘。
- 打开应用自动加载并预热模型；停止录音后复用模型。
- 默认麦克风采集，离线英语流式识别；确认后的识别结果保存为 UTF-8 TXT。
- 独立课堂文件、相对时间戳、逐次刷新磁盘，停止时保存剩余识别结果。
- 重复讲话分别保留，错误时停止采集并保留待确认文字。

## 下载与运行

下载 `sherpa-recorder-winui-v1.0.1-full-win-x64.zip`，完整解压到可写目录，不要直接从 ZIP 中运行 EXE。Windows 11 x64；无需安装 .NET 或 Windows App SDK，也无需另行下载模型。

ZIP 已包含 [Nemotron INT8 官方模型](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemotron-speech-streaming-en-0.6b-1120ms-int8-2026-04-25.tar.bz2)，四个识别文件位于：

```text
models/sherpa-onnx-nemotron-speech-streaming-en-0.6b-1120ms-int8-2026-04-25/
  encoder.int8.onnx
  decoder.int8.onnx
  joiner.int8.onnx
  tokens.txt
```

双击 `builds/winui-v3/Recorder.App.exe` 或运行 `start.ps1`。待模型加载完成后开始记录；使用“停止并保存”或 `stop.ps1` 停止录音并保存。默认记录位于 `recordings`。

包内使用默认配置，不包含开发者的麦克风 ID、个人设置、日志或课堂记录。首次使用请允许麦克风访问。应用未做代码签名，Windows 可能显示安全提示；请核对下载来源和 `SHA256SUMS.txt`，不建议关闭系统安全保护。

## 验证与限制

已验证模型预加载、两次录音复用、停止收尾、重复讲话、长静默、601.75 秒工程音频连续回放和原生界面。工程音频不是实际课堂；印度／中东口音与专业词准确率尚未正式验收。没有独立语法纠错、强制标点或崩溃后的内存尾句恢复。

源码采用 MIT；第三方组件遵循各自许可，原文在包内 `licenses`。模型采用 NVIDIA Open Model License，模型目录附带协议副本与 NOTICE，不受本项目 MIT 许可覆盖。完整验证范围见 [validation.md](validation.md)。
