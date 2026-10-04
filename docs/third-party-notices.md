# 第三方组件与许可

本项目自己的源码使用 [MIT](../LICENSE)。该许可证不覆盖第三方运行库、模型和测试素材。

| 组件 | 原始许可／声明来源 |
| --- | --- |
| sherpa-onnx 1.13.8 | Apache-2.0；[官方 LICENSE](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/LICENSE) |
| .NET 8 运行库 | MIT 及包内 THIRD-PARTY-NOTICES.TXT |
| Windows App SDK / WinUI 3 | 所用 NuGet 包中的 license.txt、NOTICE.txt |
| ONNX Runtime | MIT 及官方 ThirdPartyNotices.txt |
| kaldi-native-fbank / kaldi-decoder / OpenFst | 各官方版本中的 LICENSE 或 COPYING |
| Eigen / KissFFT | MPL-2.0 / BSD-3-Clause，原始 COPYING 文件 |
| 其他随附 Microsoft 组件 | 各 NuGet 包中附带的许可与声明 |
| NVIDIA Nemotron 模型 | NVIDIA Open Model License；模型目录附协议 PDF 和 NOTICE |

发布包的 `licenses` 目录保存构建依赖中的许可和第三方声明，以及 sherpa-onnx 和 ONNX Runtime 的原始许可文本；这些文件保留其原始内容。该目录可能包含依赖解析涉及但未实际打包的组件声明，不意味着本程序使用了它们的所有功能。

Nemotron 模型不进入 Git 源码仓库，随 Release 完整 ZIP 分发。模型目录保留 [NVIDIA 协议](https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/)副本和归属 NOTICE，所用 ONNX／INT8 导出由 sherpa-onnx 提供，本应用不修改模型文件。测试音频为官方模型附带样本的重复组合，来源见 [测试素材说明](../tests/fixtures/README.md)；本项目不主张其原创权利。
