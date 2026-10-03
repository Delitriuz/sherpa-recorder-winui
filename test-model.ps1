param([string]$WaveFile = (Join-Path $PSScriptRoot 'tests\fixtures\same-sentence-twice.wav'), [switch]$Realtime)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'builds\winui-v3\worker\Recorder.Worker.exe'
$wavePath = (Resolve-Path -LiteralPath $WaveFile).Path
$arguments = @('--test-wav', $wavePath)
if ($Realtime) { $arguments += '--realtime' }
& $exe @arguments
if ($LASTEXITCODE -ne 0) { throw '识别测试未通过；已有录音时不会启动测试。' }
