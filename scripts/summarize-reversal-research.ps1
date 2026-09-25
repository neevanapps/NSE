param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
[System.Threading.Thread]::CurrentThread.CurrentCulture = $culture
$reports = @(Get-ChildItem -LiteralPath $RunDirectory -Filter '2026-*.json' | Sort-Object Name | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
if ($reports.Count -ne 12) { throw "Expected 12 research sessions, found $($reports.Count)." }
$lines = [System.Collections.Generic.List[string]]::new()
function Add([string]$text) { $lines.Add($text) }
function N($x) { if ($null -eq $x) { return 'NA' }; return ([double]$x).ToString('0.00', $culture) }
function Sum($rows, $field) { return [double](($rows | Measure-Object -Property $field -Sum).Sum) }
function Mean($rows, $field) { $values = @($rows | Where-Object { $null -ne $_.$field }); if (!$values.Count) { return $null }; return (Sum $values $field) / $values.Count }
function Metrics($rows) {
    $rows = @($rows | Sort-Object Exit)
    $net = Sum $rows Net
    $positive = Sum @($rows | Where-Object Net -gt 0) Net
    $negative = -(Sum @($rows | Where-Object Net -lt 0) Net)
    $equity=0.0; $peak=0.0; $dd=0.0
    foreach ($row in $rows) { $equity += $row.Net; $peak=[Math]::Max($peak,$equity); $dd=[Math]::Max($dd,$peak-$equity) }
    $win=0.0; if ($rows.Count) { $win=100.0*@($rows | Where-Object Net -gt 0).Count/$rows.Count }
    $pf=$null; if ($negative -gt 0) { $pf=$positive/$negative }
    $best=0.0; if ($rows.Count) { $best=($rows | Measure-Object Net -Maximum).Maximum }
    return [pscustomobject]@{ Count=$rows.Count; Net=$net; PF=$pf; Win=$win; DD=$dd; Mfe=(Mean $rows Mfe); Mae=(Mean $rows Mae); Seconds=(Mean $rows Seconds); Best=$best }
}
$modes = @('P0','P1','C0-Call','C1-Call','C2-Call','C0-Put','C1-Put','C2-Put')
$allTrades = @{}
foreach ($mode in $modes) { $allTrades[$mode] = @($reports | ForEach-Object { $_.experiments.$mode.simulation.Trades }) }
Add '# Reversal research: complete session report'
Add ''
Add 'All results below use 12 previously inspected research sessions. September 24 and 25 are excluded. These are one-lot, conditional bid/ask execution simulations, not independently validated profits. Raw depth/last-trade field freshness is unavailable. Crossover Calls/Puts are separate one-position experiments; do not add their P&L as a portfolio. P0/P1 are a combined A/B portfolio with attribution by side.'
Add ''
Add 'Net includes modeled STT, exchange/SEBI fees, stamp and GST, plus one adverse tick per fill. MFE/MAE are LTP points, not executable liquidation values. Drawdown is closed-trade rupees, not intratrade equity drawdown. PF=NA means no losing trades (or no trades), not evidence of an infinite edge.'
Add ''
Add '## Session outcomes, each side before pooled summaries'
Add ''
Add '| Date | DTE | Rule/side | N | Net Rs | PF | Win% | Closed DD Rs | MFE pts | MAE pts | Mean seconds |'
Add '|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|'
foreach ($r in $reports) {
    foreach ($mode in $modes) {
        $sides = if ($mode -like 'P*') { @('A','B') } else { @('all') }
        foreach ($side in $sides) {
            $trades=@($r.experiments.$mode.simulation.Trades | Where-Object { $side -eq 'all' -or $_.Side -eq $side })
            $m=Metrics $trades; $label=if($side -eq 'all'){$mode}else{"$mode-$side"}
            Add "| $(([datetime]$r.Date).ToString('yyyy-MM-dd')) | $($r.Dte) | $label | $($m.Count) | $(N $m.Net) | $(N $m.PF) | $(N $m.Win) | $(N $m.DD) | $(N $m.Mfe) | $(N $m.Mae) | $(N $m.Seconds) |"
        }
    }
}
Add ''
Add '## DTE outcomes by side'
Add ''
Add '| DTE | Sessions | Rule/side | N | Net Rs | PF | Win% | Closed DD Rs |'
Add '|---:|---:|---|---:|---:|---:|---:|---:|'
foreach ($g in ($reports | Group-Object Dte | Sort-Object {[int]$_.Name})) {
    foreach ($mode in $modes) {
        $sides=if($mode -like 'P*'){@('A','B')}else{@('all')}
        foreach ($side in $sides) {
            $m=Metrics @($g.Group | ForEach-Object { $_.experiments.$mode.simulation.Trades } | Where-Object { $side -eq 'all' -or $_.Side -eq $side })
            $label=if($side -eq 'all'){$mode}else{"$mode-$side"}
            Add "| $($g.Name) | $($g.Count) | $label | $($m.Count) | $(N $m.Net) | $(N $m.PF) | $(N $m.Win) | $(N $m.DD) |"
        }
    }
}
Add ''
Add '## A/B underlying reversal and matched controls'
Add ''
Add 'Positive values below mean movement in the expected reversal direction (A down; B up). Each signal is compared to one same-session non-A/B observation with the same preceding futures-move sign and the nearest absolute context move. Matching uses no future return. Controls may repeat; they are diagnostic, not independent samples or trading gates. Both signals and controls retain missing forward horizons as unavailable.'
Add ''
Add '| Date | Pattern | Full-surface entries | +1 hit% | +2 hit% | +4 hit% | +4 mean pts | Control +4 mean pts | Selected option opportunities | Option +1m mean% | +2m mean% | +5m mean% |'
Add '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|'
foreach ($r in $reports) {
    foreach ($side in @('A','B')) {
        $sign=if($side -eq 'A'){-1}else{1}
        $signals=@($r.patternRows | Where-Object { $_.StateEntry -and $_.Full -and $_.State -eq $side })
        $controls=@(foreach ($s in $signals) {
            $r.patternRows | Where-Object { $_.State -eq 'Other' -and [Math]::Sign($_.Move) -eq [Math]::Sign($s.Move) } |
                Sort-Object @{Expression={[Math]::Abs([Math]::Abs($_.Move)-[Math]::Abs($s.Move))}},Time | Select-Object -First 1
        })
        $hits=@(foreach($h in @('Forward1','Forward2','Forward4')) { $v=@($signals | Where-Object { $null -ne $_.$h }); if($v.Count){100.0*@($v | Where-Object { $_.$h*$sign -gt 0 }).Count/$v.Count}else{$null} })
        $opts=@($r.experiments.P0.signals | Where-Object Side -eq $side)
        $f=Mean $signals Forward4; $c=Mean $controls Forward4
        if($null -ne $f){$f*=$sign}; if($null -ne $c){$c*=$sign}
        Add "| $(([datetime]$r.Date).ToString('yyyy-MM-dd')) | $side | $($signals.Count) | $(N $hits[0]) | $(N $hits[1]) | $(N $hits[2]) | $(N $f) | $(N $c) | $($opts.Count) | $(N (Mean $opts Forward1)) | $(N (Mean $opts Forward2)) | $(N (Mean $opts Forward5)) |"
    }
}
Add ''
Add '## Crossover exact-token forward behaviour, before lifecycle P&L'
Add ''
Add '| Date | Rule | Candidate signals | +1m mean% | +2m mean% | +5m mean% | +5m positive% |'
Add '|---|---|---:|---:|---:|---:|---:|'
foreach($r in $reports){ foreach($mode in $modes | Where-Object {$_ -like 'C*'}){
    $s=@($r.experiments.$mode.signals); $valid=@($s | Where-Object {$null -ne $_.Forward5}); $hit=$null
    if($valid.Count){$hit=100.0*@($valid | Where-Object Forward5 -gt 0).Count/$valid.Count}
    Add "| $(([datetime]$r.Date).ToString('yyyy-MM-dd')) | $mode | $($s.Count) | $(N (Mean $s Forward1)) | $(N (Mean $s Forward2)) | $(N (Mean $s Forward5)) | $(N $hit) |"
}}
Add ''
Add '## Pooled summaries and concentration (read after side/session tables)'
Add ''
Add '| Rule | N | Trades/session | Net Rs | PF | Win% | Closed DD Rs | Best trade Rs | Best day Rs | Net without best day Rs | Positive days | Zero-trade days | Unresolved |'
Add '|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|'
foreach($mode in $modes){
    $m=Metrics $allTrades[$mode]; $dayNet=@($reports | ForEach-Object { Sum @($_.experiments.$mode.simulation.Trades) Net })
    $bestDay=($dayNet | Measure-Object -Maximum).Maximum
    $zero=@($reports | Where-Object { @($_.experiments.$mode.simulation.Trades).Count -eq 0 }).Count
    $unresolved=($reports | ForEach-Object {$_.experiments.$mode.simulation.Unresolved} | Measure-Object -Sum).Sum
    Add "| $mode | $($m.Count) | $(N ($m.Count/12.0)) | $(N $m.Net) | $(N $m.PF) | $(N $m.Win) | $(N $m.DD) | $(N $m.Best) | $(N $bestDay) | $(N ($m.Net-$bestDay)) | $(@($dayNet | Where-Object {$_ -gt 0}).Count) | $zero | $unresolved |"
}
Add ''
Add '## What the filters suppress'
Add ''
Add 'This holds each baseline trade entry and exit fixed, then marks whether the candidate retained that exact token/time signal. Sequential P&L above also includes changed position availability. Delayed C2 entries are not called suppressed C0 trades: their entry timing differs.'
Add ''
Add '| Comparison | Suppressed baseline trades | Profitable suppressed | Suppressed net Rs | Retained baseline net Rs |'
Add '|---|---:|---:|---:|---:|'
$suppressionExamples=[System.Collections.Generic.List[string]]::new()
foreach($pair in @(@('P0','P1'),@('C0-Call','C1-Call'),@('C0-Put','C1-Put'))){
    $suppressed=@(); $retained=@()
    foreach($r in $reports){
        $keys=@{}; foreach($s in $r.experiments.($pair[1]).signals){$keys["$($s.Token)|$($s.Time.ToString('O'))"]=$true}
        foreach($t in $r.experiments.($pair[0]).simulation.Trades){if($keys.ContainsKey("$($t.Token)|$($t.Decision.ToString('O'))")){$retained+=$t}else{$suppressed+=$t}}
    }
    Add "| $($pair[0]) -> $($pair[1]) | $($suppressed.Count) | $(@($suppressed | Where-Object Net -gt 0).Count) | $(N (Sum $suppressed Net)) | $(N (Sum $retained Net)) |"
    $lostWinner=$suppressed | Sort-Object Net -Descending | Select-Object -First 1
    if($null -ne $lostWinner){
        $suppressionExamples.Add("Suppression example $($pair[1]): token $($lostWinner.Token) at $($lostWinner.Decision.ToString('O')); baseline net Rs $(N $lostWinner.Net). The candidate waits because its gap/extension condition fails, even though this baseline trade later earns that result. This is a counterfactual observation, never a future-aware decision.")
    }
}
foreach($example in $suppressionExamples){Add ''; Add $example}
Add ''
Add '## Data and causality audit'
Add ''
Add '| Date | Option ticks | Empty token intervals | Unwarmed token intervals | Late-for-cadence ticks | Invalid one-lot quotes | Reference A/B entries | Receipt/freshness-corrected entries |'
Add '|---|---:|---:|---:|---:|---:|---:|---:|'
$traceCount=0; $sampleCount=0
foreach($r in $reports){
    $late=Sum $r.coverage ReceiptAfterCadence; $unwarm=1500*$r.coverage.Count-(Sum $r.coverage Warm)
    $ref=@($r.referencePatterns | Where-Object {$_.StateEntry -and $_.Full}).Count
    $correct=@($r.patternRows | Where-Object {$_.StateEntry -and $_.Full}).Count
    Add "| $(([datetime]$r.Date).ToString('yyyy-MM-dd')) | $(Sum $r.coverage Count) | $(Sum $r.coverage Empty) | $unwarm | $late | $(Sum $r.coverage InvalidQuotes) | $ref | $correct |"
    foreach($s in $r.samples){
        if($s.RawTicks.Count){$avg=Mean $s.RawTicks Price; if([Math]::Abs($avg-$s.bar.Average) -gt 0.0000001){throw 'Independent mean mismatch'}}
        $sampleCount++
    }
    foreach($trace in $r.traces){
        $t=$trace.Trade
        if($t.Entry -lt $t.Decision.AddSeconds(1) -or $t.Exit -lt $t.ExitDecision.AddSeconds(1) -or $t.Exit -le $t.Entry){throw 'Noncausal trade'}
        $entry=@($trace.RawTicks | Where-Object Id -eq $t.EntryId); $exit=@($trace.RawTicks | Where-Object Id -eq $t.ExitId)
        if($entry.Count -ne 1 -or $exit.Count -ne 1){throw 'Fill tick ID not uniquely traceable'}
        if($entry[0].Received -gt $t.Entry -or $exit[0].Received -gt $t.Exit){throw 'Fill before receipt'}
        if($entry[0].Ask -gt $t.Buy -or $exit[0].Bid -lt $t.Sell){throw 'Fill better than quote'}
        $traceCount++
    }
}
Add ''
Add "Independent report checks: $sampleCount sampled cadence means and $traceCount complete trade paths checked; fill IDs unique, receipt/order chronology causal, no price better than the recorded side of spread. See each session JSON for full raw ticks. Invalid/late snapshots are counted, not silently called fresh trade prints."
Add ''
Add '### Frozen signal reference reconciliation'
Add ''
Add 'Reference-only exchange-clock states are independently rebuilt from raw ticks and compared to the existing option-momentum signal export. This checks signal reproduction; no legacy P&L or forward record is overwritten.'
Add ''
Add '| Date | Rebuilt | Existing historical record | Exact state/time key matches |'
Add '|---|---:|---:|---:|'
$historical=Import-Csv 'vc-option-momentum-signals.csv'
foreach($r in $reports){
    $date=([datetime]$r.Date).ToString('yyyy-MM-dd')
    $saved=@($historical | Where-Object TradingDate -eq $date)
    $reference=@($r.referencePatterns | Where-Object {$_.StateEntry -and $_.Full})
    $keys=@{}; foreach($s in $saved){$keys["$($s.Pattern)|$($s.SignalTimeIST)"]=$true}
    $matched=0; foreach($s in $reference){$ist=([datetimeoffset]$s.Time).ToOffset([timespan]::FromHours(5.5)).ToString('HH:mm:ss.fff'); if($keys.ContainsKey("$($s.State)|$ist")){$matched++}}
    Add "| $date | $($reference.Count) | $($saved.Count) | $matched |"
}
Add ''
Add '## Buy, wait, exit examples'
Add ''
foreach($mode in @('P0','P1','C0-Call','C1-Put','C2-Put')){
    $trades=$allTrades[$mode]
    foreach($kind in @('best','worst')){
        $t=if($kind -eq 'best'){$trades | Sort-Object Net -Descending | Select-Object -First 1}else{$trades | Sort-Object Net | Select-Object -First 1}
        if($null -eq $t){continue}
        Add "- $mode ${kind}: $($t.Side), token $($t.Token). BUY decision $($t.Decision.ToString('O')); actual modeled fill $($t.Entry.ToString('O')) at $(N $t.Buy). EXIT decision $($t.ExitDecision.ToString('O')), fill $($t.Exit.ToString('O')) at $(N $t.Sell), $($t.Reason). Net Rs $(N $t.Net); LTP MFE/MAE $(N $t.Mfe)/$(N $t.Mae) points."
    }
    $wait=$reports | ForEach-Object {$_.experiments.$mode.simulation.Decisions} | Where-Object Action -eq 'WAIT' | Select-Object -First 1
    if($null -ne $wait){Add "- $mode WAIT at $($wait.Time.ToString('O')): $($wait.Reason)."}
}
Add ''
Add 'All tried configurations and the initial failed timing run are recorded in docs/REVERSAL_RESEARCH_2026-09-25.md. Future settled sessions are required for independent evaluation; no rule is promoted by this report.'
$path=Join-Path $RunDirectory 'REPORT.md'
[System.IO.File]::WriteAllLines((Join-Path (Get-Location) $path),$lines)
Write-Output "Wrote $path; validated $sampleCount cadence samples and $traceCount full trade paths."

