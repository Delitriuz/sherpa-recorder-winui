# 依赖与模型

| 组件 | 锁定版本 |
| --- | --- |
| .NET SDK | 8.0.425，见 global.json |
| 目标框架 | .NET 8，Windows x64 |
| Microsoft.WindowsAppSDK | 1.8.260921001 |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 |
| org.k2fsa.sherpa.onnx | 1.13.8 |

NuGet 使用 `https://api.nuget.org/v3/index.json`，配置见 `NuGet.Config`。包缓存位于 `deps/nuget/packages`；CLI 工作目录位于 `deps/dotnet-home`。发布目录携带 .NET、Windows App SDK 及匹配版本的 Windows x64 sherpa-onnx 原生运行库。

## 识别模型

默认模型：`sherpa-onnx-nemotron-speech-streaming-en-0.6b-1120ms-int8-2026-04-25`。encoder、decoder、joiner 均为 INT8，配套 tokens.txt。CPU 识别，默认 2 线程。

- [sherpa-onnx 官方项目](https://github.com/k2-fsa/sherpa-onnx)
- [模型下载](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemotron-speech-streaming-en-0.6b-1120ms-int8-2026-04-25.tar.bz2)

Release 完整 ZIP 已将模型放在 `models` 下，并配好默认相对目录；源码构建需自行下载模型。模型不进入 Git 源码仓库。本机现有配置继续只读引用相邻模型目录，打包时只复制模型，不修改原件。

本应用没有加入独立语法纠错、二次识别模型、翻译或云服务。第三方代码、模型与测试素材的使用和分发须遵守其原始许可。
