$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'builds\winui-v3\Recorder.App.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "构建不存在：$exe" }
Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot
