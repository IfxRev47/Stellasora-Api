[CmdletBinding()]
param(
    [string] $ListenAddress,
    [int] $WireGuardPort = 51820,
    [int] $WebPort = 8081,
    [switch] $PromptHandshakeKey,
    [string] $HandshakeKey
)

$ErrorActionPreference = 'Stop'
if ($PromptHandshakeKey -and $PSBoundParameters.ContainsKey('HandshakeKey')) {
    throw 'Use either -PromptHandshakeKey or -HandshakeKey.'
}

# --- locate the addon (absolute path, works from any working directory) ---
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$addon = Join-Path $scriptDir 'reverse\tools\protocol\stella_mitm.py'
if (-not (Test-Path -LiteralPath $addon -PathType Leaf)) { throw "Stella addon not found: $addon" }

# --- locate mitmweb.exe (PATH first, then common install locations) ---
$candidates = @()
$cmd = Get-Command mitmweb.exe -CommandType Application -ErrorAction SilentlyContinue
if ($cmd) { $candidates += $cmd.Source }
$candidates += (Join-Path $env:USERPROFILE '.local\bin\mitmweb.exe')
$candidates += (Join-Path $env:APPDATA 'uv\tools\mitmproxy\Scripts\mitmweb.exe')
$candidates += (Join-Path $env:USERPROFILE '.local\pipx\venvs\mitmproxy\Scripts\mitmweb.exe')

$mitmweb = $null
foreach ($c in $candidates) {
    if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { $mitmweb = $c; break }
}
if (-not $mitmweb) { throw 'mitmweb.exe not found. Install mitmproxy (uv tool install mitmproxy).' }

# --- pick the LAN IPv4 address (interface that actually has the default route) ---
if (-not $ListenAddress) {
    $ip = $null
    $net = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
        Where-Object { $_.IPv4DefaultGateway -ne $null -and $_.NetAdapter.Status -eq 'Up' } |
        Select-Object -First 1
    if ($net) { $ip = ($net.IPv4Address | Select-Object -First 1).IPAddress }
    if (-not $ip) {
        $ip = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -notlike '172.1[6-9].*' -and $_.IPAddress -notlike '172.2*' -and $_.IPAddress -notlike '172.3[01].*' } |
            Select-Object -First 1 -ExpandProperty IPAddress
    }
    if (-not $ip) { throw 'No LAN IPv4 found. Pass -ListenAddress 192.168.x.x' }
    $ListenAddress = $ip
}
if ($ListenAddress -like '127.*' -or $ListenAddress -eq '0.0.0.0') {
    throw 'Pass the PC LAN IPv4 address so the phone can reach the tunnel.'
}

$keyFile = Join-Path $env:USERPROFILE '.mitmproxy\wireguard-capture-keys.conf'
$mode = "wireguard:${keyFile}@${ListenAddress}:$WireGuardPort"

Write-Host "mitmweb   : $mitmweb"
Write-Host "addon     : $addon"
Write-Host "WireGuard : ${ListenAddress}:$WireGuardPort (UDP)"
Write-Host "Web UI    : http://127.0.0.1:$WebPort/"
Write-Host 'Use the generated WireGuard profile on the phone. Ctrl+C stops capture.'

$previousKey = $env:STELLA_HANDSHAKE_KEY
try {
    if ($PromptHandshakeKey) {
        $secureKey = Read-Host 'Stella handshake key' -AsSecureString
        $env:STELLA_HANDSHAKE_KEY = [System.Net.NetworkCredential]::new('', $secureKey).Password
    } elseif ($PSBoundParameters.ContainsKey('HandshakeKey')) {
        $env:STELLA_HANDSHAKE_KEY = $HandshakeKey
    }
    if ($env:STELLA_HANDSHAKE_KEY -or $PromptHandshakeKey -or $PSBoundParameters.ContainsKey('HandshakeKey')) {
        if ([Text.Encoding]::UTF8.GetByteCount([string]$env:STELLA_HANDSHAKE_KEY) -ne 32) {
            throw 'Stella handshake key must be exactly 32 UTF-8 bytes.'
        }
        Write-Host 'Stella request decryption: handshake key configured.'
    } else {
        Write-Host 'Stella request decryption: set STELLA_HANDSHAKE_KEY, -HandshakeKey, or -PromptHandshakeKey.'
    }

    & $mitmweb --mode $mode --web-host 127.0.0.1 --web-port $WebPort --no-web-open-browser -s $addon
    $captureExitCode = $LASTEXITCODE
} finally {
    $env:STELLA_HANDSHAKE_KEY = $previousKey
}
exit $captureExitCode
