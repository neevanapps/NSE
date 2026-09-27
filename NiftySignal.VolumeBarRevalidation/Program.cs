using NiftySignal.VolumeBarData;

var inputRoot = args
    .FirstOrDefault(a => a.StartsWith("--root=", StringComparison.Ordinal))
    ?.Substring("--root=".Length)
    ?? "research-ticks-v2";

var outputDirectory = args
    .FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))
    ?.Substring("--out=".Length)
    ?? "research-6500-revalidation";

return await VolumeBar6500RevalidationRunner.RunAsync(
    inputRoot,
    outputDirectory,
    CancellationToken.None);
