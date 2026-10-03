# 测试

测试素材位于 `fixtures`，来源见 [素材说明](fixtures/README.md)。生成文件均写入 `results`，不进入源码交付。

## 编译与基础验证

在项目根目录运行：

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'deps/dotnet-home'
dotnet restore tests/Recorder.Tests/Recorder.Tests.csproj --configfile NuGet.Config
dotnet build tests/Recorder.Tests/Recorder.Tests.csproj -c Release --no-restore -p:UseAppHost=false -p:SelfContained=false
dotnet artifacts/bin/Recorder.Tests/Release/net8.0-windows/win-x64/Recorder.Tests.dll
```

验证原生设备枚举、WAV 输入、模型自动加载、IPC 多连接与重连、失败报告、单实例和正常退出。运行前正常关闭应用，避免连接到正在使用的工作进程。

## 可选集成测试

- 测试程序加 `--capture-probe`：实际采集默认麦克风约 3 秒，检查采样格式。
- 加 `--recording-probe`：不可写目录失败、两次实际录音复用模型、停止脚本及 UTF-8 尾部保存。仅在不录课时运行。
- `builds/winui-v3/Recorder.App.exe --smoke-test`：真实 XAML、加载状态、实时修订、重复结果、错误恢复、设置与历史；生成七张预览，不打开麦克风、不改变配置。
- `builds/winui-v3/Recorder.App.exe --desktop-test`：正常入口自动加载模型、托盘、第二实例重定向及正常退出，不打开麦克风。
- `./test-model.ps1`：默认回放本项目同句两遍素材；用 `-WaveFile` 指定外部 WAV，`-Realtime` 实时回放。
- `./tests/tail-silence.ps1`：生成长静默后单次讲话的 WAV，检查静默和无额外尾静音的收尾。

外部 WAV 必须为 16 kHz 单声道 PCM16 或 float32。工程素材不代表真实课堂准确率；加速回放延迟不代表实时字幕延迟。验证结论见 [验收范围](../docs/validation.md)。
