$ErrorActionPreference = 'Stop'
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'SherpaRecorder.WinUI.v1', [System.IO.Pipes.PipeDirection]::InOut)
try {
    $pipe.Connect(3000)
    $reader = [System.IO.StreamReader]::new($pipe, [System.Text.Encoding]::UTF8, $false, 4096, $true)
    $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 4096, $true)
    $writer.AutoFlush = $true
    $null = $reader.ReadLine()
    $writer.WriteLine('{"Type":"Stop","Version":1}')
    while ($true) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw '连接已断开，尚未确认保存结果。' }
        $message = $line | ConvertFrom-Json
        if ($message.State -eq 'Error') { throw "记录失败，未确认文字仍在工作进程中：$($message.Text)" }
        if ($message.State -in @('Idle', 'Stopped')) { Write-Host 'WinUI 工作进程已确认停止。'; break }
    }
} finally { $pipe.Dispose() }
