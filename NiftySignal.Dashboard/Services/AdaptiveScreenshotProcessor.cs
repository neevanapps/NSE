using System.Security.Cryptography;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;
using NiftySignal.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NiftySignal.Dashboard.Services;

public sealed class AdaptiveScreenshotProcessor(IAdaptiveScreenshotRenderer renderer, ITelegramDocumentSender sender,
    IOptions<AdaptiveScreenshotOptions> options)
{
    public async Task ProcessOneAsync(AdaptiveObserverDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var job = await db.ScreenshotJobs.Where(x => x.Status == AdaptiveScreenshotStatus.Pending && x.NextAttemptUtc <= now)
            .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (job is null) return;
        var path = options.Value.FilePath(job);
        job.Status = AdaptiveScreenshotStatus.Capturing;
        job.Attempts++;
        await db.SaveChangesAsync(ct);
        try
        {
            if (job.ImageSha256 is null || !File.Exists(path))
            {
                await renderer.CaptureAsync(job, path, ct);
                await using (var stream = File.OpenRead(path))
                    job.ImageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                job.CapturedAtUtc = DateTimeOffset.UtcNow;
                job.Caption = $"NIFTY adaptive | {job.TradeDate:yyyy-MM-dd} | {job.Kind}\nCompleted BarSeq {job.TargetBarSeq} | trigger IST {job.TriggeredAtUtc.ToOffset(TimeSpan.FromHours(5.5)):HH:mm:ss}\nCaptured IST {job.CapturedAtUtc.Value.ToOffset(TimeSpan.FromHours(5.5)):HH:mm:ss} | job {job.Id}\nBOTH / NOTIONAL / ATM±2 | completed rows only\nDashboard SHA {BuildIdentity.Current().CommitSha}";
                await db.SaveChangesAsync(ct);
            }
            await using (var stream = File.OpenRead(path))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (digest != job.ImageSha256) throw new InvalidOperationException("Stored screenshot hash differs.");
                if (stream.Length > 45_000_000) throw new InvalidOperationException("Screenshot exceeds safe Telegram document limit.");
            }
        }
        catch (Exception ex) when (job.Status == AdaptiveScreenshotStatus.Capturing)
        {
            job.Status = AdaptiveScreenshotStatus.Pending;
            job.NextAttemptUtc = now.AddSeconds(60);
            job.LastError = $"Capture failed ({ex.GetType().Name}); safe retry queued.";
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }
        job.Status = AdaptiveScreenshotStatus.Sending;
        await db.SaveChangesAsync(ct); // Durable before the first upload byte.
        TelegramDocumentResult delivery;
        try { delivery = await sender.SendAsync(path, job.Caption!, ct); }
        catch { delivery = new(TelegramDocumentOutcome.Uncertain, Error: "Sender interrupted; delivery uncertain; no automatic resend."); }
        job.Status = delivery.Outcome switch {
            TelegramDocumentOutcome.Sent => AdaptiveScreenshotStatus.Sent,
            TelegramDocumentOutcome.Rejected => AdaptiveScreenshotStatus.Pending,
            _ => AdaptiveScreenshotStatus.DeliveryUncertain };
        job.TelegramMessageId = delivery.MessageId;
        job.SentAtUtc = delivery.Outcome == TelegramDocumentOutcome.Sent ? DateTimeOffset.UtcNow : null;
        job.NextAttemptUtc = now.AddSeconds(delivery.RetryAfterSeconds);
        job.LastError = delivery.Error;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
