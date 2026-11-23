param([switch]$StopOnly, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$projectDirectory = Join-Path $PSScriptRoot 'Stella.LocalGateway'
$runtimeDirectory = Join-Path $taskRoot 'work/gateway-runtime'
$pidFile = Join-Path $taskRoot 'work/gateway-background.json'

function Stop-BackgroundGateway {
    if (-not (Test-Path -LiteralPath $pidFile)) { return }
    $record = Get-Content -LiteralPath $pidFile -Raw | ConvertFrom-Json
    $running = Get-CimInstance Win32_Process -Filter "ProcessId = $($record.pid)"
    if ($running) {
        $expectedDll = Join-Path $runtimeDirectory 'Stella.LocalGateway.dll'
        if ($running.Name -ne 'dotnet.exe' -or -not $running.CommandLine.Contains($expectedDll)) {
            throw '记录的 PID 已被其他进程使用，未停止该进程。'
        }
        Stop-Process -Id $record.pid
        Wait-Process -Id $record.pid -Timeout 10 -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $pidFile -Force
}

if ($StopOnly) { Stop-BackgroundGateway; Write-Output '后台网关已停止'; return }
if (-not $NoBuild) {
    dotnet build $projectDirectory
    if ($LASTEXITCODE -ne 0) { throw '编译失败，未替换后台网关' }
}
Stop-BackgroundGateway
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'bin/Debug/net10.0') |
    Copy-Item -Destination $runtimeDirectory -Recurse -Force
$runtimeDll = Join-Path $runtimeDirectory 'Stella.LocalGateway.dll'
$dotnetPath = (Get-Command dotnet).Source
$started = Start-Process -FilePath $dotnetPath -ArgumentList @('"' + $runtimeDll + '"') `
    -WorkingDirectory $projectDirectory -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $taskRoot 'work/gateway-live.log') `
    -RedirectStandardError (Join-Path $taskRoot 'work/gateway-live.err.log')
@{ pid=$started.Id; runtime=$runtimeDirectory } | ConvertTo-Json | Set-Content -LiteralPath $pidFile
Write-Output "后台网关已启动，PID $($started.Id)，运行目录 $runtimeDirectory"
Write-Output 'VS 编译目录未被后台进程占用；在 VS 启动调试前请运行 server/start-local.ps1 -StopOnly。'
