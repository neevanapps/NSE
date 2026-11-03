namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Settings of the read-only agent feed ("AgentFeed" section of appsettings). The JSONL files are written to <c>&lt;RootDirectory&gt;/agent-feed/yyyy-MM-dd.jsonl</c>; a relative root is resolved against the
/// Dashboard's install folder. The HTTP endpoint is always available on the loopback interface only; it has no setting that widens that.
/// </summary>
public sealed class AgentFeedOptions
{
    public const string SectionName = "AgentFeed";
    public const string FeedFolderName = "agent-feed";

    /// <summary>Append one JSON line per completed bar. Off = the endpoint still works, nothing is written to disk.</summary>
    public bool JsonlEnabled { get; set; } = true;

    /// <summary>Folder that CONTAINS the agent-feed folder.</summary>
    public string RootDirectory { get; set; } = "data";

    /// <summary>How often the writer looks for newly completed bars.</summary>
    public int PollSeconds { get; set; } = 3;

    /// <summary>A completed bar is written only after it is this old, so the sidecar rows the Host persists right after a bar completes are in place and the line is not written half empty.</summary>
    public int SettleSeconds { get; set; } = 5;

    public string FeedDirectory()
    {
        var root = Path.IsPathRooted(RootDirectory) ? RootDirectory : Path.Combine(AppContext.BaseDirectory, RootDirectory);
        return Path.Combine(root, FeedFolderName);
    }

    public string FilePath(DateOnly tradeDate) => Path.Combine(FeedDirectory(), tradeDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
}
