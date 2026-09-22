using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Persistence;

// Daily VM -> desktop incremental replication (2026-09-15), sitting alongside the existing
// full dump/restore tool at scripts/sync-vm-database.ps1 -- that one is the "fresh full copy"
// path (pg_dump/pg_restore, drops and recreates niftysignal_vm_copy every run); this one is the
// "just today's new rows" path, so a daily habit doesn't mean re-copying the VM's entire,
// ever-growing DB (ticks alone: 3.86M+ rows and climbing) every single day. Both write to the
// SAME local database, niftysignal_vm_copy -- never the desktop's own niftysignal database,
// which local dev/testing already uses (see sync-vm-database.ps1's own doc comment) -- and this
// tool assumes niftysignal_vm_copy already exists with the right schema (i.e. at least one full
// sync has already been run); it never creates or migrates a database itself.
//
// Only two tables are ever touched here -- Instruments and Ticks -- because those are the only
// two NiftySignal.BacktestData/CadencePopulator.cs actually reads to build the backtest tables
// NiftySignal.MetricTrials runs against; everything else in niftysignal_vm_copy (ScoreSnapshots,
// PaperTrades, ...) stays whatever the last full sync left it as, deliberately not kept live-fresh
// by this tool.
//
// The VM's Postgres is already reachable directly from the desktop (confirmed by
// sync-vm-database.ps1's own pg_dump -h <vm> usage) -- over the same Tailscale link the dashboard
// and deploy.ps1 already use -- so this tool needs no WinRM/remote-execution step at all: both
// "export" (reads the VM) and "import" (writes locally) run right here on the desktop, just
// against different connection strings. Neither this tool nor the orchestrating PowerShell
// script (sync-vm-data-incremental.ps1) ever hardcodes a password -- both take a full Npgsql
// connection string via --connection, assembled by the caller from wherever it already keeps that
// secret (see the PS script for exactly where).
//
// Usage:
//   dotnet run --project NiftySignal.DataSync -- export --table=Instruments --since=0
//       --out=path.ndjson --connection="<VM connection string>"
//   dotnet run --project NiftySignal.DataSync -- import --table=Ticks --in=path.ndjson
//       --connection="<local niftysignal_vm_copy connection string>"
//   dotnet run --project NiftySignal.DataSync -- max-id --table=Ticks --connection="<...>"
//
// export/max-id print a single "SYNC_RESULT ..." line to stdout as their last line of output --
// the orchestrating script parses that line and nothing else, so don't add other stdout chatter
// after it.

string? TryArg(string name) =>
    args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 3)..];

string RequireArg(string name) =>
    TryArg(name) ?? throw new ArgumentException($"Missing required --{name}=");

bool HasFlag(string name) =>
    args.Contains($"--{name}", StringComparer.OrdinalIgnoreCase);

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: export|import|max-id --table=Instruments|Ticks [--since=N] [--out=path] [--in=path] --connection=<connection string>");
    return 1;
}

var verb = args[0];
var table = RequireArg("table");
if (table is not ("Instruments" or "Ticks"))
{
    Console.Error.WriteLine($"Unknown --table={table}. Must be Instruments or Ticks.");
    return 1;
}

var connectionString = RequireArg("connection");
var dbOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(connectionString).Options;
await using var db = new NiftySignalDbContext(dbOptions);

return verb switch
{
    "export" => await ExportAsync(),
    "import" => await ImportAsync(),
    "max-id" => await MaxIdAsync(),
    _ => Fail($"Unknown verb '{verb}'. Must be export, import, or max-id."),
};

int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

async Task<int> ExportAsync()
{
    var since = long.Parse(TryArg("since") ?? "0");
    var outPath = RequireArg("out");

    // --gzip exists because of a real, live problem (2026-09-16): a multi-million-row Ticks
    // catch-up as plain NDJSON is 1.5-2GB, and Copy-Item -FromSession (WinRM/PSRemoting) is
    // painfully slow at moving a file that size -- it base64-encodes and wraps everything in
    // SOAP/XML envelopes, not built for bulk transfer. Highly repetitive line-oriented JSON
    // compresses well, so this cuts what actually crosses the wire by a large factor. Streamed
    // directly into the compressor -- the uncompressed file is never materialized on disk at all.
    await using var fileStream = File.Create(outPath);
    await using Stream outputStream = HasFlag("gzip") ? new GZipStream(fileStream, CompressionLevel.Fastest) : fileStream;
    await using var writer = new StreamWriter(outputStream);

    var maxId = since;
    var count = 0;

    if (table == "Instruments")
    {
        var rows = db.Instruments.AsNoTracking().Where(i => i.Id > since).OrderBy(i => i.Id).AsAsyncEnumerable();
        await foreach (var row in rows)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(row));
            maxId = row.Id;
            count++;
        }
    }
    else
    {
        var rows = db.Ticks.AsNoTracking().Where(t => t.Id > since).OrderBy(t => t.Id).AsAsyncEnumerable();
        await foreach (var row in rows)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(row));
            maxId = row.Id;
            count++;
        }
    }

    // maxId stays at `since` (not advanced) when count=0, so a caller that blindly persists this
    // as its new watermark never regresses on an empty day.
    Console.WriteLine($"SYNC_RESULT count={count} maxId={maxId}");
    return 0;
}

async Task<int> ImportAsync()
{
    var inPath = RequireArg("in");
    const int batchSize = 2000;

    // Id is preserved, not zeroed and reassigned -- live-caught 2026-09-16: this originally reset
    // Id to 0 ("the source database's Id means nothing here"), which quietly broke the one thing
    // that actually needs to be true for niftysignal_vm_copy specifically -- scripts/
    // sync-vm-database.ps1's pg_restore ALREADY preserves the VM's original Ids verbatim (a plain
    // Postgres data restore, not an app-level insert), so a caller computing "what's the local
    // watermark" by querying niftysignal_vm_copy's own max Id (max-id against its connection
    // string, not a separately-tracked file) only gets the right answer if THIS tool's own writes
    // preserve the same Id space. Both tables' Id columns are Npgsql's default "GENERATED BY
    // DEFAULT AS IDENTITY" (not ALWAYS), which permits an explicit value on insert without any
    // special OVERRIDING clause -- confirmed, this just works. The side benefit: since Id is each
    // table's own primary key, re-importing a file whose rows are already present now fails loudly
    // with a primary-key violation instead of Ticks (no separate unique constraint of its own)
    // silently doubling every row -- a real correctness improvement, not just a watermark fix.
    var imported = 0;
    var batch = 0;

    await foreach (var line in ReadLinesAsync(inPath, HasFlag("gzip")))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        if (table == "Instruments")
        {
            var row = JsonSerializer.Deserialize<Instrument>(line) ?? throw new InvalidDataException($"Could not parse Instrument line: {line}");
            db.Instruments.Add(row);
        }
        else
        {
            var row = JsonSerializer.Deserialize<Tick>(line) ?? throw new InvalidDataException($"Could not parse Tick line: {line}");
            db.Ticks.Add(row);
        }

        imported++;
        batch++;
        if (batch >= batchSize)
        {
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            batch = 0;
        }
    }

    if (batch > 0)
    {
        await db.SaveChangesAsync();
    }

    Console.WriteLine($"SYNC_RESULT count={imported}");
    return 0;
}

async IAsyncEnumerable<string> ReadLinesAsync(string path, bool gzip)
{
    if (!gzip)
    {
        await foreach (var line in File.ReadLinesAsync(path))
        {
            yield return line;
        }
        yield break;
    }

    await using var fileStream = File.OpenRead(path);
    await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
    using var reader = new StreamReader(gzipStream);
    while (await reader.ReadLineAsync() is { } line)
    {
        yield return line;
    }
}

async Task<int> MaxIdAsync()
{
    // --before-date exists for exactly one situation: catching up a gap left by the FIRST-EVER
    // bootstrap run (which anchors the watermark to "right now", not "start of today" -- see
    // sync-vm-data-incremental.ps1's own -CatchUpSinceDate for why) or a day the daily job
    // silently didn't run at all. Same IST-midnight-to-UTC convention CadencePopulator.cs's own
    // dayStart already uses, restated here rather than shared -- this project's established
    // precedent for a single small literal like this (see EntryRuleEvaluator's own doc comment).
    var beforeDateArg = TryArg("before-date");
    long maxId;

    if (table == "Instruments")
    {
        var query = db.Instruments.AsQueryable();
        if (beforeDateArg is not null)
        {
            var beforeDate = DateOnly.ParseExact(beforeDateArg, "yyyy-MM-dd");
            query = query.Where(i => i.AsOfDate < beforeDate);
        }
        maxId = await query.OrderByDescending(i => i.Id).Select(i => i.Id).FirstOrDefaultAsync();
    }
    else
    {
        var query = db.Ticks.AsQueryable();
        if (beforeDateArg is not null)
        {
            var beforeDate = DateOnly.ParseExact(beforeDateArg, "yyyy-MM-dd");
            var istOffset = TimeSpan.FromHours(5.5);
            var beforeUtc = new DateTimeOffset(beforeDate.ToDateTime(TimeOnly.MinValue), istOffset).ToUniversalTime();
            query = query.Where(t => t.ExchangeTimestamp < beforeUtc);
        }
        maxId = await query.OrderByDescending(t => t.Id).Select(t => t.Id).FirstOrDefaultAsync();
    }

    Console.WriteLine($"SYNC_RESULT maxId={maxId}");
    return 0;
}
