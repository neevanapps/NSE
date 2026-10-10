using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
// 2026-09-25 correction: the paragraph that used to be here claimed the VM's Postgres is
// reachable directly from the desktop, so neither verb needed WinRM. That was wrong, and
// contradicted scripts/sync-vm-data-incremental.ps1's OWN later correction (2026-09-15, still in
// that file) -- a live run found `Test-NetConnection <vm> -Port 5432` succeeds on ping but fails
// on the actual TCP connect, so Postgres is NOT reachable off-VM. The orchestrating script
// already reflects the real topology (Invoke-Command runs "export" ON the VM, over WinRM,
// against the VM's own local Postgres; "import" runs locally, against niftysignal_vm_copy) --
// this file's own comment just never got updated to match. Restated correctly: export always
// runs wherever the VM's Postgres is actually reachable from (the VM itself, today), import
// always runs wherever niftysignal_vm_copy is actually reachable from (the desktop). Neither this
// tool nor the orchestrating PowerShell script ever hardcodes a password -- both take a full
// Npgsql connection string via --connection, assembled by the caller from wherever it already
// keeps that secret (see the PS script for exactly where).
//
// 2026-09-25 perf rewrite: export/import were originally EF Core row-by-row (AsAsyncEnumerable +
// per-row JsonSerializer.Serialize on export; db.Add + SaveChangesAsync batches of 2000 on
// import) -- exactly the per-row-ORM-overhead risk docs/PLAN.md's own section 1.7 flagged BEFORE
// this tool was ever built ("EF's per-row overhead is noticeable at ~1.9M rows/day... consider
// raw Npgsql bulk copy"), never actually followed until now. With NIFTY+SENSEX+BANKNIFTY ticks
// combined, a full day's incremental sync was taking ~100 minutes. Both verbs now use Npgsql's
// RAW BINARY COPY stream (`BeginRawBinaryCopyAsync`/`NpgsqlRawBinaryCopyStream`) -- a byte-level
// passthrough between Postgres's own COPY protocol and the (gzip'd) file, with NO row-level
// decoding/materialization in this process at all. Row count and watermark (max Id) are obtained
// via separate, cheap index-range queries (COUNT/MAX against the Id primary key), never by
// decoding the copied rows -- see each verb's own comment for why that's still correct. Column
// order is implicit in binary COPY (no column list = the table's own defined order) and depends
// on niftysignal_vm_copy's schema staying identical to the VM's (both come from the same EF Core
// migrations, same assumption this tool's Id-preservation logic already depended on -- not a NEW
// assumption introduced by this rewrite, just restated here since it now matters for column order
// too, not just Id values).
//
// One real behavior change from the old row-by-row import, flagged rather than silently
// introduced: the old code committed every 2000-row batch separately, so a primary-key violation
// partway through a file left the earlier batches already committed (a partial import). A single
// COPY command runs as one atomic operation server-side -- a violation anywhere in the file now
// rolls back the ENTIRE import for that table, not just the batch containing it. This is more
// correct (no partial-import state to reason about) but means a retry after a genuine duplicate
// needs the whole file re-examined, not just "resume from where it stopped."
//
// Usage:
//   dotnet run --project NiftySignal.DataSync -- export --table=Instruments --since=0
//       --out=path.ndjson --connection="<VM connection string>"
//   dotnet run --project NiftySignal.DataSync -- import --table=Ticks --in=path.ndjson
//       --connection="<local niftysignal_vm_copy connection string>" --expected-count=N
//       (--expected-count is the count= value ExportAsync printed for this same file; import
//       trusts and echoes it back rather than re-counting a potentially huge table -- see
//       ImportAsync's own comment. Omit it only for a manual, throwaway run; it then reports 0.)
//   dotnet run --project NiftySignal.DataSync -- max-id --table=Ticks --connection="<...>"
//
// export/max-id print a single "SYNC_RESULT ..." line to stdout as their last line of output --
// the orchestrating script parses that line and nothing else, so don't add other stdout chatter
// after it.

string? TryArg(string name) =>
    args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))?[(name.Length + 3)..];

string RequireArg(string name) =>
    TryArg(name) ?? throw new ArgumentException($"Missing required --{name}=");

// 2026-09-25 bugfix, live-caught on the first real run after the COPY rewrite: the EF row-by-row
// code never needed this, because `db.Instruments`/`db.Ticks` were translated through EF's
// configured table mapping automatically -- NiftySignal.Persistence/Configurations/
// InstrumentConfiguration.cs and TickConfiguration.cs both call `builder.ToTable("instruments")`/
// `builder.ToTable("ticks")` (lowercase; the Postgres table is NOT the same spelling as the CLR
// type/DbSet/this tool's own --table= argument, which are all PascalCase "Instruments"/"Ticks").
// Raw SQL has no EF translation layer, so it must map the CLI's logical table name to the actual
// physical Postgres table name itself, or a double-quoted "Instruments" (exact-case, since
// quoting makes Postgres identifiers case-sensitive) simply doesn't exist -- confirmed live:
// `relation "Instruments" does not exist` / `relation "Ticks" does not exist`.
string PhysicalTableName(string logicalTable) => logicalTable == "Instruments" ? "instruments" : "ticks";

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
    var quotedTable = $"\"{PhysicalTableName(table)}\"";

    // Row count and max Id come from a tiny separate aggregate query against the Id primary key
    // (an index range scan, not a sequential scan) -- cheap regardless of table size, and
    // deliberately NOT derived from the raw copy below, which never decodes a single row.
    long maxId = since;
    long count = 0;
    await using (var countConn = new NpgsqlConnection(connectionString))
    {
        await countConn.OpenAsync();
        await using var countCmd = new NpgsqlCommand(
            $"SELECT COUNT(*), COALESCE(MAX(\"Id\"), {since}) FROM {quotedTable} WHERE \"Id\" > {since}", countConn);
        await using var reader = await countCmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            count = reader.GetInt64(0);
            maxId = reader.GetInt64(1);
        }
    }

    // --gzip exists because of a real, live problem (2026-09-16): a multi-million-row Ticks
    // catch-up is 1.5-2GB raw, and Copy-Item -FromSession (WinRM/PSRemoting) is painfully slow at
    // moving a file that size -- it base64-encodes and wraps everything in SOAP/XML envelopes,
    // not built for bulk transfer. Postgres's own binary COPY format is already dense and
    // compresses further; streamed directly into the compressor so the uncompressed bytes are
    // never materialized on disk.
    await using var fileStream = File.Create(outPath);
    await using Stream outputStream = HasFlag("gzip") ? new GZipStream(fileStream, CompressionLevel.Fastest) : fileStream;

    if (count > 0)
    {
        // Raw binary COPY: Postgres streams its own wire-format bytes straight out; this process
        // never deserializes a row into a .NET object at all. ORDER BY Id keeps the file in the
        // same order a resumed/partial sync would expect, matching the old row-by-row behavior.
        await using var copyConn = new NpgsqlConnection(connectionString);
        await copyConn.OpenAsync();
        var copySql = $"COPY (SELECT * FROM {quotedTable} WHERE \"Id\" > {since} ORDER BY \"Id\") TO STDOUT (FORMAT BINARY)";
        await using var rawStream = await copyConn.BeginRawBinaryCopyAsync(copySql);
        await rawStream.CopyToAsync(outputStream);
    }

    // maxId stays at `since` (not advanced) when count=0, so a caller that blindly persists this
    // as its new watermark never regresses on an empty day.
    Console.WriteLine($"SYNC_RESULT count={count} maxId={maxId}");
    return 0;
}

async Task<int> ImportAsync()
{
    var inPath = RequireArg("in");
    var quotedTable = $"\"{PhysicalTableName(table)}\"";

    // Id is preserved, not zeroed and reassigned -- live-caught 2026-09-16: this originally reset
    // Id to 0 ("the source database's Id means nothing here"), which quietly broke the one thing
    // that actually needs to be true for niftysignal_vm_copy specifically -- scripts/
    // sync-vm-database.ps1's pg_restore ALREADY preserves the VM's original Ids verbatim (a plain
    // Postgres data restore, not an app-level insert), so a caller computing "what's the local
    // watermark" by querying niftysignal_vm_copy's own max Id (max-id against its connection
    // string, not a separately-tracked file) only gets the right answer if THIS tool's own writes
    // preserve the same Id space. Both tables' Id columns are Npgsql's default "GENERATED BY
    // DEFAULT AS IDENTITY" (not ALWAYS); COPY FROM (like pg_restore's own COPY-based data load)
    // always uses the incoming explicit values for such a column, no special OVERRIDING clause
    // needed -- confirmed, this just works, same as it already did for the old row-by-row INSERTs.
    // The side benefit is unchanged too: since Id is each table's own primary key, a file whose
    // rows are already present fails the COPY loudly with a primary-key violation instead of
    // Ticks (no separate unique constraint of its own) silently doubling every row.

    await using var fileStream = File.OpenRead(inPath);
    await using Stream inputStream = HasFlag("gzip") ? new GZipStream(fileStream, CompressionMode.Decompress) : fileStream;

    // Raw binary COPY, the mirror image of ExportAsync's -- bytes flow straight from the
    // (decompressed) file into Postgres's own COPY protocol, no row ever becomes a .NET object in
    // this process. No column list on the destination table means "all columns, in the table's
    // own defined order" -- correct as long as niftysignal_vm_copy's schema matches the VM's,
    // which this tool has always assumed (see the top-of-file comment).
    await using (var copyConn = new NpgsqlConnection(connectionString))
    {
        await copyConn.OpenAsync();
        var copySql = $"COPY {quotedTable} FROM STDIN (FORMAT BINARY)";
        await using var rawStream = await copyConn.BeginRawBinaryCopyAsync(copySql);
        await inputStream.CopyToAsync(rawStream);
    }

    // 2026-10-01 bugfix, live-caught on the first real multi-million-row import after the COPY
    // rewrite: this used to re-derive the imported count with `SELECT COUNT(*) WHERE "Id" >
    // maxIdBefore` on a connection (`preConn`) that had sat open and idle for the entire COPY --
    // for a 33.2M-row Ticks batch that COUNT scan itself timed out
    // (`System.TimeoutException: Timeout during reading attempt`), ironically making the
    // "verification" step the slowest, least reliable part of the whole import. It was also
    // redundant: `COPY FROM STDIN` is one atomic server-side operation (see the Id-preservation
    // comment above) -- if it returns without throwing, every row in the input stream was
    // committed, full stop. There is nothing left to verify by re-querying the table.
    // ExportAsync already counted these exact rows cheaply (an Id-indexed range COUNT, done once,
    // before the file was ever written) and printed it as its own `SYNC_RESULT count=`; the
    // orchestrating script captures that value and now passes it straight through as
    // --expected-count=, so this just echoes back the number the caller already trusts rather
    // than re-counting a potentially huge table. Falls back to "unknown" (0) only for a manual,
    // ad-hoc `import` invocation that omits the flag -- not a path any script here takes.
    var expectedCount = long.Parse(TryArg("expected-count") ?? "0");

    Console.WriteLine($"SYNC_RESULT count={expectedCount}");
    return 0;
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
