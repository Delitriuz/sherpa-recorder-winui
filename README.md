# 课堂记录

基于 WinUI 3 与 sherpa-onnx 的 Windows x64 离线英语课堂记录应用。直接采集麦克风，不使用 Windows 实时字幕或云端服务。

[下载 Windows x64 发布包](https://github.com/Delitriuz/sherpa-recorder-winui/releases/latest) · [首次运行说明](docs/release-notes.md) · [MIT 许可证](LICENSE)

## 使用

1. 运行 `start.ps1`，或双击 `builds/winui-v3/Recorder.App.exe`。
2. 应用自动加载并预热模型；完成后，填写课程名称、选择麦克风，点击“开始记录”。
3. 实时区显示尚在修订的文字；下方列表只显示已写入磁盘的句子。
4. 点击“停止并保存”正常收尾，也可运行 `stop.ps1`。下一次开始会复用已加载模型。
5. 点击“后台运行”隐藏窗口，双击托盘恢复。关闭窗口默认正常停止并退出，可在设置中改为隐藏到托盘。

每堂课生成独立 UTF-8 TXT，包含课程名称、开始时间和相对音频时间戳 `[HH:mm:ss]`。不要单独移动 EXE，发布目录中的运行库和 `worker` 均需保留。

## 记录规则

- 模型端点判定并稳定等待后提交，默认等待 1.8 秒；临时文字不逐字追加。
- 每个最终结果只保存一次；真实重复讲话分别保留，不按全文去重。
- 每次提交刷新磁盘，正常停止时排空音频并保存尾句。
- 保存或采集失败会停止采集并显示错误；未确认文字可另存。强杀、崩溃或断电可能丢失内存中的尾句。
- 加载模型不打开麦克风、不创建课堂记录。停止后模型继续驻留，退出应用时释放。
- 相对时间戳表示提交时处理到的音频位置，不是逐词对齐时间。

## 开发

需要 Windows x64 和 .NET SDK 8.0.425。首次克隆后，从[模型来源](docs/dependencies.md)下载并解压模型到 `models`，将 `config/recorder.example.json` 复制为 `config/recorder.json`。正常关闭应用后运行：

```powershell
.\build.ps1
```

脚本只更新当前发布目录，运行中拒绝覆盖。依赖与 CLI 缓存全部保存在项目内。用户配置为 `config/recorder.json`，默认示例为 `config/recorder.example.json`。

构建后运行 `./package.ps1 -Version v1.0.0` 可生成带模型的完整发布 ZIP 与 SHA-256 校验文件，输出位于 `artifacts/releases`。模型默认取自项目 `models`，也可用 `-ModelDirectory` 指定已下载的模型目录；仅复制识别所需文件，不包含个人配置或课堂数据。第三方许可按锁定来源下载至项目缓存，再随包分发。

| 目录 | 内容 |
| --- | --- |
| `src/Recorder.App` | WinUI 界面、托盘和工作进程连接 |
| `src/Recorder.Core` | 配置、消息协议、原生音频与 WAV 输入 |
| `src/Recorder.Worker` | 模型生命周期、流式识别与文本提交 |
| `config` | 配置示例和本机配置 |
| `tests` | 验证程序、测试素材与运行结果 |
| `docs` | 架构、依赖来源和验收范围 |
| `deps`、`artifacts`、`builds` | 本地依赖、编译缓存和发布产物 |
| `logs`、`recordings` | 运行日志和课堂记录 |

源码交付不包含用户数据、模型、构建缓存或历史备份，排除规则见 `.gitignore`。当前发布目录沿用既有路径，启动入口无需变更。

## 模型与验证

默认使用项目 `models` 下的 Nemotron INT8 模型。可通过配置的 `ModelDirectory` 指向其他位置；模型相对路径须只含英文字符，项目根目录可含中文。源码仓库不包含模型或预编译 EXE；Release 的完整 ZIP 已包含应用、运行库和模型，解压即可使用，无需单独下载模型。

- [架构与接口](docs/architecture.md)
- [锁定依赖与模型来源](docs/dependencies.md)
- [验证结果与已知限制](docs/validation.md)
- [运行测试](tests/README.md)

## 许可

本项目源码采用 [MIT](LICENSE)。第三方运行库、模型和测试素材不受本项目 MIT 许可证覆盖，详见 [第三方声明](docs/third-party-notices.md)。
