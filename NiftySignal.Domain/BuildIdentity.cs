using System.Reflection;

namespace NiftySignal.Domain;

public sealed record BuildIdentity(string SourceBranch, string CommitSha, DateTimeOffset? BuildUtc)
{
    public static BuildIdentity Current(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty, StringComparer.Ordinal);

        metadata.TryGetValue("SourceBranch", out var branch);
        metadata.TryGetValue("CommitSha", out var sha);
        metadata.TryGetValue("BuildUtc", out var buildText);
        DateTimeOffset? buildUtc = DateTimeOffset.TryParse(buildText, out var parsed) ? parsed : null;

        return new BuildIdentity(
            string.IsNullOrWhiteSpace(branch) ? "unknown" : branch,
            string.IsNullOrWhiteSpace(sha) ? "unknown" : sha,
            buildUtc);
    }
}
