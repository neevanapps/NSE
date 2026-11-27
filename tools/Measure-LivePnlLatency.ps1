<#
  Measures how long a quote takes to become a readable live P&L mark, while positions are open. Read-only: one psql session with
  default_transaction_read_only=on, sampling pev2.pev2_live_marks. psql asks for the password itself (or uses pgpass); this script never takes one.
  Run it on the database machine, so the sample instant and the quote instants share one clock.

  Not included: the Dashboard's own refresh (a 1 s timer, then two reads and the render to the browser).
  -FromCsv re-summarises an earlier sample file without connecting.
#>
param(
    [string] $Server = 'localhost',
    [int] $Port = 5432,
    [string] $Database,
    [string] $User,
    [ValidateRange(10, 3600)][int] $Seconds = 300,
    [ValidateRange(100, 1000)][int] $IntervalMs = 200,
    [string] $OutputDirectory = '.',
    [string] $Psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    [string] $FromCsv
)
$ErrorActionPreference = 'Stop'

if (-not $FromCsv) {
    if (-not $Database -or -not $User) { throw 'Give -Database and -User (the paper ledger database and a role that may SELECT pev2.pev2_live_marks).' }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $FromCsv = Join-Path $OutputDirectory ("live-pnl-samples-{0}.csv" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $count = [int][Math]::Ceiling($Seconds * 1000 / $IntervalMs)
    $interval = ($IntervalMs / 1000.0).ToString([Globalization.CultureInfo]::InvariantCulture)
    $script = "$FromCsv.sql"
    Set-Content -LiteralPath $script -Encoding ASCII -Value ((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'live-pnl-latency.sql') -Raw) + "\watch i=$interval c=$count")
    $previous = $env:PGOPTIONS
    $env:PGOPTIONS = '-c default_transaction_read_only=on'
    try {
        & $Psql -X --csv -t -h $Server -p $Port -d $Database -U $User -v ON_ERROR_STOP=1 -f $script -o $FromCsv
        if ($LASTEXITCODE -ne 0) { throw "psql failed (exit $LASTEXITCODE)." }
    }
    finally {
        $env:PGOPTIONS = $previous
        Remove-Item -LiteralPath $script -Force
    }
}

# data rows only (psql may add \watch titles): observed_at,position_id,quote_tick_id,quote_to_published_ms,quote_to_readable_ms
$rows = @(Get-Content -LiteralPath $FromCsv | Where-Object { $_ -match '^\d{4}-\d\d-\d\d [^,]+,[^,]+,\d+,-?\d+,-?\d+\s*$' } | ForEach-Object {
        $f = $_.Trim().Split(',')
        [pscustomobject]@{ Observed = $f[0]; Key = "$($f[1])|$($f[2])"; Published = [double]$f[3]; Readable = [double]$f[4] }
    })
if ($rows.Count -eq 0) { Write-Host 'No sample: no Fresh mark was seen (no open position, marks not published, or nothing sampled). NOT a pass.'; exit 2 }

# the quotes already published when sampling began have an unknown first-readable instant: left out
$seen = @{}
$rows | Where-Object { $_.Observed -eq $rows[0].Observed } | ForEach-Object { $seen[$_.Key] = $true }
$first = @(foreach ($r in $rows) { if (-not $seen.ContainsKey($r.Key)) { $seen[$r.Key] = $true; $r } })
if ($first.Count -eq 0) { Write-Host 'No new quote was published while sampling. NOT a pass.'; exit 2 }

function Rank([double[]] $sorted, [double] $q) { $sorted[[Math]::Max(0, [int][Math]::Ceiling($sorted.Count * $q) - 1)] }
foreach ($measure in @(@('quote -> published', 'Published'), @('quote -> readable', 'Readable'))) {
    $v = [double[]]@($first | ForEach-Object { $_.($measure[1]) } | Sort-Object)
    [pscustomobject]@{ Measure = $measure[0]; Samples = $v.Count; P50Ms = (Rank $v 0.5); P95Ms = (Rank $v 0.95); MaxMs = $v[-1] }
}
