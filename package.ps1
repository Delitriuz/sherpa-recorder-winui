param(
    [string]$Version = 'v1.0.1',
    [string]$ModelDirectory = (Join-Path $PSScriptRoot 'models\sherpa-onnx-nemotron-speech-streaming-en-0.6b-1120ms-int8-2026-04-25')
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^v\d+\.\d+\.\d+$') { throw '版本格式应为 v1.0.0。' }
$projectRoot = $PSScriptRoot
$output = Join-Path $projectRoot 'artifacts\releases'
$null = New-Item -ItemType Directory -Path $output -Force
$zip = Join-Path $output "sherpa-recorder-winui-$Version-full-win-x64.zip"
if (Test-Path -LiteralPath $zip) { throw "发布包已存在，请先保留或移走它：$zip" }
$stage = Join-Path $output ("stage-" + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $projectRoot 'builds\winui-v3'
if (-not (Test-Path -LiteralPath (Join-Path $app 'worker\Recorder.Worker.exe'))) { throw '请先运行 build.ps1。' }
$ModelDirectory = (Resolve-Path -LiteralPath $ModelDirectory).Path
$modelFiles = @('encoder.int8.onnx','decoder.int8.onnx','joiner.int8.onnx','tokens.txt')
foreach ($name in $modelFiles) {
    $file = Join-Path $ModelDirectory $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "模型缺少文件：$name" }
}
$licenseCache = Join-Path $projectRoot 'deps\release-licenses'
$null = New-Item -ItemType Directory -Path $licenseCache -Force
$sources = @{
    'sherpa-onnx-LICENSE.txt' = 'https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/v1.13.8/LICENSE'
    'onnxruntime-LICENSE.txt' = 'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.28.2/LICENSE'
    'onnxruntime-ThirdPartyNotices.txt' = 'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.28.2/ThirdPartyNotices.txt'
    'kaldi-native-fbank-LICENSE.txt' = 'https://raw.githubusercontent.com/csukuangfj/kaldi-native-fbank/v1.22.3/LICENSE'
    'kaldi-decoder-LICENSE.txt' = 'https://raw.githubusercontent.com/k2-fsa/kaldi-decoder/v0.3.0/LICENSE'
    'openfst-COPYING.txt' = 'https://raw.githubusercontent.com/csukuangfj/openfst/v1.8.5-2026-07-09/COPYING'
    'eigen-COPYING.MPL2.txt' = 'https://gitlab.com/libeigen/eigen/-/raw/5.0.1/COPYING.MPL2'
    'kissfft-COPYING.txt' = 'https://raw.githubusercontent.com/mborgerding/kissfft/febd4caeed32e33ad8b2e0bb5ea77542c40f18ec/COPYING'
    'kissfft-BSD-3-Clause.txt' = 'https://raw.githubusercontent.com/mborgerding/kissfft/febd4caeed32e33ad8b2e0bb5ea77542c40f18ec/LICENSES/BSD-3-Clause'
    'NVIDIA-Open-Model-License.pdf' = 'https://www.nvidia.com/content/dam/en-zz/Solutions/license-agreements/enterprise-software/nvidia-open-model-license-agreements-24-10-2025.pdf'
}
foreach ($item in $sources.GetEnumerator()) {
    $target = Join-Path $licenseCache $item.Key
    if (-not (Test-Path -LiteralPath $target)) { Invoke-WebRequest -Uri $item.Value -OutFile $target }
}
$null = New-Item -ItemType Directory -Path (Join-Path $stage 'builds\winui-v3') -Force
foreach ($file in Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Extension -ne '.pdb' }) {
    $relative = $file.FullName.Substring($app.Length + 1)
    $target = Join-Path $stage ("builds\winui-v3\" + $relative)
    $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
foreach ($name in @('README.md','LICENSE','recorder.project.json','start.ps1','stop.ps1')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $stage -Recurse
$null = New-Item -ItemType Directory -Path (Join-Path $stage 'config') -Force
foreach ($name in @('recorder.json','recorder.example.json')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'config\recorder.example.json') -Destination (Join-Path $stage "config\$name")
}
$settings = Get-Content (Join-Path $stage 'config\recorder.json') -Raw | ConvertFrom-Json
$modelTarget = Join-Path $stage $settings.ModelDirectory
$null = New-Item -ItemType Directory -Path $modelTarget -Force
foreach ($name in $modelFiles) { Copy-Item -LiteralPath (Join-Path $ModelDirectory $name) -Destination $modelTarget }
if (Test-Path -LiteralPath (Join-Path $ModelDirectory 'README.md')) { Copy-Item -LiteralPath (Join-Path $ModelDirectory 'README.md') -Destination $modelTarget }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\model-NOTICE.txt') -Destination (Join-Path $modelTarget 'NOTICE.txt')
Copy-Item -LiteralPath (Join-Path $licenseCache 'NVIDIA-Open-Model-License.pdf') -Destination $modelTarget
$licenses = Join-Path $stage 'licenses'
$null = New-Item -ItemType Directory -Path $licenses -Force
$packages = Join-Path $projectRoot 'deps\nuget\packages'
foreach ($assets in @('Recorder.App','Recorder.Worker')) {
    $manifest = Get-Content (Join-Path $projectRoot "artifacts\obj\$assets\project.assets.json") -Raw | ConvertFrom-Json
    foreach ($library in $manifest.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $folder = Join-Path $packages $library.Name
        foreach ($file in Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -match 'license|notice|copying' }) {
            $target = Join-Path $licenses $library.Name
            $null = New-Item -ItemType Directory -Path $target -Force
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}
Copy-Item -LiteralPath $licenseCache -Destination $licenses -Recurse
$forbidden = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Extension -in @('.pdb','.log','.pid','.wav') })
if ($forbidden.Count) { throw '发布包包含调试或用户数据，请检查暂存目录。' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Host "发布包：$zip"
Write-Host "SHA256：$hash"
