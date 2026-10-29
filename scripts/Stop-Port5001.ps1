$ErrorActionPreference = 'Stop'

$processIds = @(
    Get-NetTCPConnection -LocalPort 5001 -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique
)

if ($processIds.Count -eq 0) {
    Write-Output 'No process is listening on TCP port 5001.'
    exit 0
}

foreach ($processId in $processIds) {
    if ($processId -eq 4) {
        Write-Error 'Port 5001 is owned by the Windows System process (PID 4); refusing to stop it.'
        continue
    }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        continue
    }

    Stop-Process -Id $processId -Force
    Write-Output "Stopped $($process.ProcessName) (PID $processId)."
}
