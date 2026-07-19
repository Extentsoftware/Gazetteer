#Requires -Version 5.1
<#
.SYNOPSIS
  Keeps Gazetteer kubectl port-forwards alive, restarting them when the connection drops.

.DESCRIPTION
  Forwards:
    - Postgres:        localhost:35432       -> svc/postgres
    - Elasticsearch:   localhost:9200, 9300  -> statefulset/gazetteer-elasticsearch

  Uses services/statefulsets (not ephemeral pod names) so renames after restarts still work.
  Ctrl+C stops all forwards cleanly.

.EXAMPLE
  .\scripts\keep-port-forwards.ps1
#>

$ErrorActionPreference = 'Stop'

$RequiredContext = 'microk8s'
$RestartDelaySeconds = 2
$Namespace = 'default'

$Forwards = @(
    [pscustomobject]@{
        Name = 'postgres'
        # Stable across Deployment pod renames; maps local 35432 -> service 35432 -> target 5432
        Args = @('port-forward', 'svc/postgres', '35432:35432', '-n', $Namespace)
    }
    [pscustomobject]@{
        Name = 'elasticsearch'
        # Stable StatefulSet name; same ports as a direct pod forward
        Args = @('port-forward', 'statefulset/gazetteer-elasticsearch', '9200:9200', '9300:9300', '-n', $Namespace)
    }
)

function Assert-KubectlContext {
    $context = (& kubectl config current-context 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($context)) {
        throw "Could not read kubectl current-context. Is kubectl configured?"
    }
    if ($context -ne $RequiredContext) {
        throw "Refusing to port-forward: current context is '$context' (expected '$RequiredContext')."
    }
    Write-Host "kubectl context: $context" -ForegroundColor Green
}

function Get-KubectlPath {
    $cmd = Get-Command kubectl -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw "kubectl not found on PATH."
    }
    return $cmd.Source
}

function Start-ForwardProcess {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $KubectlPath,
        [Parameter(Mandatory)] [string[]] $Args
    )

    Write-Host "[$Name] starting: kubectl $($Args -join ' ')" -ForegroundColor Cyan

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $KubectlPath
    $psi.Arguments = ($Args | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true

    # Action blocks run in a separate runspace — keep logging self-contained (no outer functions).
    $null = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -MessageData $Name -Action {
        $line = $EventArgs.Data
        $name = $Event.MessageData
        if ([string]::IsNullOrWhiteSpace($line)) { return }
        if ($line -match 'Forwarding from') {
            Write-Host "[$name] $line" -ForegroundColor Green
        }
        else {
            Write-Host "[$name] $line"
        }
    }
    $null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -MessageData $Name -Action {
        $line = $EventArgs.Data
        $name = $Event.MessageData
        if ([string]::IsNullOrWhiteSpace($line)) { return }
        Write-Host "[$name] $line" -ForegroundColor Yellow
    }

    $null = $proc.Start()
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()
    return $proc
}

function Stop-AllForwards {
    param([hashtable] $Processes)

    Get-EventSubscriber -ErrorAction SilentlyContinue |
        Where-Object { $_.SourceObject -is [System.Diagnostics.Process] } |
        ForEach-Object { Unregister-Event -SourceIdentifier $_.SourceIdentifier -ErrorAction SilentlyContinue }

    foreach ($name in @($Processes.Keys)) {
        $proc = $Processes[$name]
        if ($null -ne $proc -and -not $proc.HasExited) {
            Write-Host "[$name] stopping pid $($proc.Id)..." -ForegroundColor DarkGray
            try { $proc.Kill() } catch { }
            try { $null = $proc.WaitForExit(3000) } catch { }
        }
        if ($null -ne $proc) {
            $proc.Dispose()
        }
        $Processes.Remove($name)
    }

    Get-CimInstance Win32_Process -Filter "Name = 'kubectl.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'port-forward.*(postgres|gazetteer-elasticsearch)' } |
        ForEach-Object {
            Write-Host "Killing leftover kubectl pid $($_.ProcessId)" -ForegroundColor DarkGray
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
}

Assert-KubectlContext
$kubectlPath = Get-KubectlPath

$processes = @{}
$nextStart = @{}
foreach ($forward in $Forwards) {
    $nextStart[$forward.Name] = [datetime]::UtcNow
}

Write-Host ""
Write-Host "Port-forwards running (Ctrl+C to stop):" -ForegroundColor Green
Write-Host "  Postgres        -> localhost:35432"
Write-Host "  Elasticsearch   -> localhost:9200, localhost:9300"
Write-Host ""

try {
    while ($true) {
        foreach ($forward in $Forwards) {
            $name = $forward.Name
            $proc = $processes[$name]

            if ($null -ne $proc -and -not $proc.HasExited) {
                continue
            }

            if ($null -ne $proc) {
                $code = $proc.ExitCode
                Write-Host "[$name] disconnected (exit $code); restarting in ${RestartDelaySeconds}s..." -ForegroundColor Yellow
                $proc.Dispose()
                $processes.Remove($name)
                $nextStart[$name] = [datetime]::UtcNow.AddSeconds($RestartDelaySeconds)
            }

            if ([datetime]::UtcNow -ge $nextStart[$name]) {
                $processes[$name] = Start-ForwardProcess -Name $name -KubectlPath $kubectlPath -Args $forward.Args
            }
        }

        Start-Sleep -Milliseconds 500
    }
}
finally {
    Write-Host "`nStopping port-forwards..." -ForegroundColor Cyan
    Stop-AllForwards -Processes $processes
    Write-Host "Done." -ForegroundColor Green
}
