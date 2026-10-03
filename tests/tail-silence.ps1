$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$inputPath = Join-Path $projectRoot 'tests\fixtures\same-sentence-twice.wav'
$bytes = [System.IO.File]::ReadAllBytes($inputPath)
if ([System.Text.Encoding]::ASCII.GetString($bytes, 36, 4) -ne 'data') { throw '工程素材 WAV 结构不符合预期。' }
# 原素材是两次相同讲话，中间 2 秒静默；取第一次讲话，前置 30 秒静默，不添加尾部静默。
$speechBytes = [int](($bytes.Length - 44 - 64000) / 2)
$silenceBytes = 30 * 16000 * 2
$output = [byte[]]::new(44 + $silenceBytes + $speechBytes)
[Array]::Copy($bytes, 0, $output, 0, 44)
[Array]::Copy([BitConverter]::GetBytes([int]($output.Length - 8)), 0, $output, 4, 4)
[Array]::Copy([BitConverter]::GetBytes([int]($output.Length - 44)), 0, $output, 40, 4)
[Array]::Copy($bytes, 44, $output, 44 + $silenceBytes, $speechBytes)
$wavePath = Join-Path $PSScriptRoot 'results\tail-silence.wav'
$null = New-Item -ItemType Directory -Path (Split-Path $wavePath -Parent) -Force
[System.IO.File]::WriteAllBytes($wavePath, $output)
& (Join-Path $projectRoot 'test-model.ps1') -WaveFile $wavePath -Realtime
