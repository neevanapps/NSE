---
name: vm-deploy-check
description: Read-only health check of the live NiftySignal deployment on the Windows VM (Tailscale IP 100.105.67.79) — confirms Tailscale reachability, both Windows services are running, a redeploy actually landed (fresh binaries, process restarted after them, not before), and tails the Host log for recent activity and errors. Use this whenever the user asks to "check the VM", "verify the deploy", "is the VM up", "did the deploy work", "check if it's running", or after the user says they've deployed/redeployed/restarted something and wants it confirmed — also useful proactively before or after any session where a fix is about to go live. Never redeploys, restarts, or changes anything — verification only.
---

# VM deploy check

A read-only verification checklist for the live deployment, distilled from the same sequence run
by hand many times already this session. Confirms the system is actually healthy rather than
assuming a deploy or restart worked.

**This skill never modifies the VM.** It doesn't redeploy, restart a service, or change
configuration — if something looks wrong, report it clearly and let the user decide the next
step. Per `AGENTS.md`, don't trigger a restart during live market hours without being asked, and
that applies doubly to a skill that isn't even meant to write anything.

## Step 1 — Tailscale reachability (no VM credentials needed)

Before attempting anything that needs Windows credentials, check whether the VM is even online:

```powershell
& "C:\Program Files\Tailscale\tailscale.exe" status | Select-String "100.105.67.79"
```

This reports online/offline and, if offline, "last seen X ago". **If it's offline, stop here and
report that** — don't attempt a WinRM session against an unreachable host; it'll just time out
slowly. An offline VM is itself the finding: nothing has been ingesting ticks since it went
offline, which matters a lot if the user is relying on today's data.

## Step 2 — Service and deploy-freshness check (needs Windows admin credentials)

If Tailscale shows it online, this step needs Windows admin credentials for the VM. If they
haven't been supplied in the conversation, ask for them — never hardcode or guess credentials.

Build a `PSCredential` and run **one** `Invoke-Command` batch covering everything below, to
minimize round trips (each separate WinRM call has noticeable latency):

```powershell
$pass = ConvertTo-SecureString '<password>' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('<username>', $pass)
$s = New-PSSession -ComputerName 100.105.67.79 -Credential $cred -ErrorAction Stop

$result = Invoke-Command -Session $s -ScriptBlock {
    $svc = Get-Service -Name NiftySignalHost, NiftySignalDashboard -ErrorAction SilentlyContinue |
        Select-Object Name, Status, StartType
    $hostDll = Get-Item "C:\Naveen\NiftySignal.Host\NiftySignal.Host.dll" -ErrorAction SilentlyContinue
    $dashDll = Get-Item "C:\Naveen\NiftySignal.Dashboard\NiftySignal.Dashboard.dll" -ErrorAction SilentlyContinue
    $hostProc = Get-Process -Name "NiftySignal.Host" -ErrorAction SilentlyContinue
    $dashProc = Get-Process -Name "NiftySignal.Dashboard" -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Services = $svc
        HostDllWrite = $hostDll.LastWriteTime; HostProcStart = $hostProc.StartTime
        DashDllWrite = $dashDll.LastWriteTime; DashProcStart = $dashProc.StartTime
    }
}
$result.Services | Format-Table -AutoSize | Out-String
Write-Output "HostDllWrite: $($result.HostDllWrite)  HostProcStart: $($result.HostProcStart)"
Write-Output "DashDllWrite: $($result.DashDllWrite)  DashProcStart: $($result.DashProcStart)"
```

What to check:
- Both services `Running`, `StartType Automatic`.
- **Process start time is after the DLL's write time** — a process that started *before* the
  latest deploy is still running the old binary in memory even though the files on disk changed;
  that's a deploy that landed but didn't take effect (the service needs restarting).
- Compare the DLL write time against `git log -1` locally (run `git -C D:\Projects\NSE log -1
  --format="%H %cI"` or equivalent) — a deploy timestamp meaningfully older than the latest local
  commit means that commit likely isn't live yet.

## Step 3 — Log tail

In the same or a follow-up `Invoke-Command`, find and tail the latest Host log:

```powershell
$logInfo = Invoke-Command -Session $s -ScriptBlock {
    $logDir = "C:\Naveen\NiftySignal.Host\logs"
    $latest = Get-ChildItem -Path $logDir -Filter "*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $tail = Get-Content -Path $latest.FullName -Tail 40
    $errs = Get-Content -Path $latest.FullName | Select-String -Pattern "ERR|FATAL|Exception|Unhandled" -CaseSensitive:$false
    [pscustomobject]@{ File = $latest.FullName; Tail = $tail; Errors = $errs }
}
```

Report both: what the recent activity looks like (e.g. clean "Score cadence" heartbeat lines vs.
"Outside market hours" waiting, which is normal pre-market/post-close, not a fault) and whether
any `ERR`/`FATAL`/`Exception`/`Unhandled` lines showed up. The Dashboard has its own `logs\`
folder under `C:\Naveen\NiftySignal.Dashboard\` if that side needs checking too.

## Step 4 — Optional: DB/schema check

Only do this if the user specifically wants schema or data checked (e.g. after a migration was
part of the deploy). The connection string lives in the VM's own
`C:\Naveen\NiftySignal.Host\appsettings.Local.json` — read it **inside** the `Invoke-Command`
block so the password stays on the VM rather than crossing back to the local session unless it's
actually needed. `psql.exe` is under `C:\Program Files\PostgreSQL\<version>\bin\psql.exe` —
locate it dynamically (`Get-ChildItem "C:\Program Files\PostgreSQL" -Directory`) rather than
hardcoding a version number, since it can change across VM rebuilds.

A useful query set: latest 5-6 rows of `__EFMigrationsHistory` (confirms migrations applied),
and a `count(*)`/`max("ComputedAt")` from `score_snapshots` (confirms fresh data is actually
landing).

## PowerShell 5.1 gotchas already hit doing this

- An inline `(if (...) {...} else {...})` inside a native-exe argument list (e.g. `& $psql -p
  (if ($x) {...} else {...})`) can fail specifically across the WinRM remoting boundary —
  compute it into a variable first, then pass the variable.
- A SQL or other native-command string containing embedded double quotes needs to be a
  **single-quoted** PowerShell string (`'SELECT "Col" FROM ...'`), not a double-quoted string
  with backslash-escaped quotes — the escaping doesn't survive being handed to a native process
  the way it would in bash.
- Always `Remove-PSSession $s` when done.

## Reporting back

Summarize plainly, don't paste raw command output as the whole answer: services up or down,
whether a fresh deploy is actually confirmed live (not just present on disk), clean logs or
specific errors found, and — if checked — DB/migration state. If everything's healthy, say so in
one line; don't manufacture caveats.
