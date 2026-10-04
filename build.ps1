$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$output = Join-Path $projectRoot 'dist\Recorder'
$running = @(Get-Process -Name Recorder.App,Recorder.Worker -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path.StartsWith($output + '\', [System.StringComparison]::OrdinalIgnoreCase) })
if ($running.Count) { throw '请先正常关闭课堂记录应用，再更新当前版本。' }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'deps\dotnet-home'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    dotnet restore .\src\Recorder.App\Recorder.App.csproj --configfile .\NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'WinUI 依赖下载失败。' }
    dotnet restore .\src\Recorder.Worker\Recorder.Worker.csproj --configfile .\NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw '识别依赖下载失败。' }
    dotnet publish .\src\Recorder.App\Recorder.App.csproj -c Release --no-restore -o $output
    if ($LASTEXITCODE -ne 0) { throw 'WinUI 发布失败。' }
    dotnet publish .\src\Recorder.Worker\Recorder.Worker.csproj -c Release --no-restore -o (Join-Path $output 'worker')
    if ($LASTEXITCODE -ne 0) { throw '工作进程发布失败。' }
    Write-Host "构建完成：$(Join-Path $output 'Recorder.App.exe')"
} finally { Pop-Location }
