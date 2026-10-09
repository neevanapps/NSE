using NiftySignal.Trading.Core.Session;

namespace NiftySignal.Trading.Core.Config;

/// <summary>
/// Versioned, immutable engine configuration (contract section 7). CP1 contains only the session policy; later checkpoints add
/// variants, risk profiles, execution and cost settings as further properties and bump <see cref="CurrentSchemaVersion"/> when the
/// meaning of an existing property changes. The hash is over the canonical serialisation, so it is independent of property or
/// dictionary order, machine culture, time zone and anything generated at run time.
/// </summary>
public sealed record TradingConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Human-assigned label of this configuration version (part of the hash: a relabel is a new version).</summary>
    public required string ConfigVersion { get; init; }

    public SessionConfig Session { get; init; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"SchemaVersion {SchemaVersion} is not supported (current {CurrentSchemaVersion}).");
        }

        if (string.IsNullOrWhiteSpace(ConfigVersion))
        {
            errors.Add("ConfigVersion is required.");
        }

        errors.AddRange(Session.Validate());
        return errors;
    }

    public string ToCanonicalJson() => CanonicalJson.Serialize(this);

    public ConfigHash ComputeHash() => CanonicalJson.Hash(this);
}
