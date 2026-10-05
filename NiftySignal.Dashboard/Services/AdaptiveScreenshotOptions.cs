namespace NiftySignal.Dashboard.Services;

public sealed class AdaptiveScreenshotOptions
{
    public const string SectionName = "AdaptiveScreenshots";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:5210";
    public string OutputDirectory { get; set; } = "screenshots";

    public Uri LocalUri()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || !uri.IsLoopback || (uri.Scheme != "http" && uri.Scheme != "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Screenshot BaseUrl must be a loopback HTTP(S) origin.");
        return uri;
    }

    public string FilePath(NiftySignal.AdaptiveObserverData.AdaptiveScreenshotJobRow job) =>
        Path.Combine(Path.GetFullPath(OutputDirectory, AppContext.BaseDirectory),
            job.TradeDate.ToString("yyyy-MM-dd"), $"adaptive-{job.Id}-{job.Kind}-bar-{job.TargetBarSeq}.png");
}
