using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>Test shorthand for variant identities: the shared test strategy is "S", so <c>Vk.S("A")</c> is the variant (S, A).</summary>
internal static class Vk
{
    public const string DefaultStrategy = "S";

    public static VariantKey S(string variantId) => new(DefaultStrategy, variantId);
}
