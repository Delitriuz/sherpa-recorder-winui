# 架构与接口

## 职责

`Recorder.App` 管理原生 XAML 界面、设备选择、课程设置、历史文件和系统托盘。它不加载识别模型；通过当前用户可访问的命名管道连接工作进程。

`Recorder.Worker` 独立负责模型缓存、音频识别与文本写入。启动时加载并预热模型，开始课程时创建新的识别流和文件，停止课程后保留模型，退出时释放资源。

`Recorder.Core` 提供配置与消息类型、项目路径解析、MMDevice/WASAPI 音频输入及 WAV 测试输入。`Contracts.cs` 定义配置和协议；`ProjectPaths.cs` 管理配置读写和根目录定位。

## 生命周期

打开应用 → Preparing → 模型就绪 Idle → Start → Loading → Recording → Stop → Stopping → Stopped。发生加载、采集或写入失败时进入 Error，并保留未确认结果；Quit 等待收尾后退出。

音频由 Windows 共享音频引擎转换为 16 kHz、单声道 float32。工作进程持续向流式识别器送入音频；Partial 仅用于实时观察，Final 对应已刷新到磁盘的结果。正常停止追加尾部处理并通知识别器输入结束，再提交剩余文字。

## 控制协议

消息以 UTF-8 JSON 单行传输，协议版本为 1。控制端发送 Start、Stop、Snapshot、Quit 等命令；工作进程返回状态快照、ModelReady、Partial、Final、Error 等事件。具体字段以 `src/Recorder.Core/Contracts.cs` 为准。

命名管道支持界面与停止脚本同时连接。会话 ID 和序号用于恢复状态与避免重复展示；它们不承担全文去重。ModelReady 表示模型可用，ModelLoads 用于验证复用次数。

UI 使用 AppInstance 保持单实例，工作进程和音频采集通过命名同步对象避免重复启动。检测到其他录音程序时禁止开始采集，不操作其进程。

## 原生交互

- 窗口：AppWindow、Mica、PerMonitorV2；默认 780×640 DIP，最小 600×560 DIP。
- 文件夹选择：Windows App SDK FolderPicker。
- 托盘：Shell_NotifyIconW。
- 音频：MMDevice API、WASAPI 共享事件驱动；不依赖 NAudio。
- 保存：每个最终结果刷新磁盘；“已保存”仅表示写入成功的结果。
