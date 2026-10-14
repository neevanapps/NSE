using System.Security.Cryptography;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;
using NiftySignal.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NiftySignal.Dashboard.Services;

public sealed class AdaptiveScreenshotProcessor(IAdaptiveScreenshotRenderer renderer, ITelegramDocumentSender sender,
    IOptions<AdaptiveScreenshotOptions> options, TimeProvider? clock = null, AdaptiveResearchService? research = null)
{
    /// <summary>
    /// Research caption: it describes the view that was actually captured (fixed-strike CORE DIRECTION / OPTION CONTEXT, completed rows only), never the legacy BOTH / NOTIONAL / ATM±2 selector.
    /// Built from the same pinned research model the screenshot rendered. The pre-live delivery test keeps its own plain caption. No secret is ever included.
    /// </summary>
    async Task<string> BuildCaptionAsync(AdaptiveScreenshotJobRow job, CancellationToken ct)
    {
        var sha = BuildIdentity.Current().CommitSha;
        if (job.Kind == AdaptiveScreenshotKind.PreLiveTest || job.SessionId is null || research is null)
        {
            return $"NIFTY Research | {job.TradeDate:yyyy-MM-dd} | {job.Kind}\nCompleted BarSeq {job.TargetBarSeq} | trigger IST {job.TriggeredAtUtc.ToOffset(TimeSpan.FromHours(5.5)):HH:mm:ss} | job {job.Id}\ncompleted rows only\nDashboard SHA {sha}";
        }

        PinnedResearchView? view = null;
        try { view = await research.LoadPinnedAsync(job.SessionId.Value, job.TargetBarSeq, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* a caption never blocks delivery of an already captured image */ }
        var caption = ResearchCaption.Build(job, view?.Model, sha);
        return caption.Length > 1000 ? caption[..1000] : caption;
    }

    public async Task ProcessOneAsync(AdaptiveObserverDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Telegram retry_after is chat-wide; do not immediately upload a different queued job.
        if (await db.ScreenshotJobs.AnyAsync(x => x.Status == AdaptiveScreenshotStatus.Pending && x.NextAttemptUtc > now
            && x.LastError != null && x.LastError.StartsWith("Telegram rejected upload"), ct)) return;
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
                job.CapturedAtUtc = (clock ?? TimeProvider.System).GetUtcNow();
                job.Caption = await BuildCaptionAsync(job, ct);
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
            // Our validation errors contain no credentials; preserve their actionable reason.
            var reason = ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
            job.LastError = $"Capture failed ({reason}); safe retry queued.";
            if (job.LastError.Length > 512) job.LastError = job.LastError[..512];
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }
        job.Status = AdaptiveScreenshotStatus.Sending;
        await db.SaveChangesAsync(ct); // Durable before the first upload byte.
        TelegramDocumentResult delivery;
        try { delivery = await sender.SendAsync(path, job.Caption!, ct); }
        catch (Exception ex)
        {
            // Exception messages/stack traces can contain the bot URL and token. Types are safe.
            var types = ex.GetType().Name;
            for (var inner = ex.InnerException; inner is not null && types.Length < 300; inner = inner.InnerException)
                types += $" -> {inner.GetType().Name}";
            delivery = new(TelegramDocumentOutcome.Uncertain,
                Error: $"Sender interrupted ({types}); delivery uncertain; no automatic resend.");
        }
        job.Status = delivery.Outcome switch {
            TelegramDocumentOutcome.Sent => AdaptiveScreenshotStatus.Sent,
            TelegramDocumentOutcome.Rejected => AdaptiveScreenshotStatus.Pending,
            _ => AdaptiveScreenshotStatus.DeliveryUncertain };
        job.TelegramMessageId = delivery.MessageId;
        var acknowledgedAt = (clock ?? TimeProvider.System).GetUtcNow();
        job.SentAtUtc = delivery.Outcome == TelegramDocumentOutcome.Sent ? acknowledgedAt : null;
        // retry_after starts when Telegram responds, not before the potentially slow browser/upload.
        job.NextAttemptUtc = acknowledgedAt.AddSeconds(delivery.RetryAfterSeconds);
        job.LastError = delivery.Error;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
