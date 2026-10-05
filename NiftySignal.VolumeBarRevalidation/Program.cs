using System.Globalization;
using NiftySignal.VolumeBarData;

var inputRoot = args
    .FirstOrDefault(a => a.StartsWith("--root=", StringComparison.Ordinal))
    ?.Substring("--root=".Length)
    ?? "research-ticks-v2";

var forwardTestArg = args.FirstOrDefault(a => a.StartsWith("--forward-test-date=", StringComparison.Ordinal))
    ?.Substring("--forward-test-date=".Length);

if (forwardTestArg is not null)
{
    var forwardTestDate = DateOnly.ParseExact(forwardTestArg, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    return await VolumeBar6500ForwardTestRunner.RunAsync(inputRoot, forwardTestDate, CancellationToken.None);
}

var outputDirectory = args
    .FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))
    ?.Substring("--out=".Length)
    ?? "research-6500-revalidation";

return await VolumeBar6500RevalidationRunner.RunAsync(
    inputRoot,
    outputDirectory,
    CancellationToken.None);
