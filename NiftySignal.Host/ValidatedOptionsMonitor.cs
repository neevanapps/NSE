using Microsoft.Extensions.Options;

namespace NiftySignal.Host;

/// <summary>Read-only access to the last-known-good value of a hot-reloaded, validated config -- see <see cref="ValidatedOptionsMonitor{TOptions,TDomain}"/>.</summary>
public interface IValidatedOptions<out TDomain>
{
    TDomain Current { get; }
}

/// <summary>
/// The validate-before-swap gate docs/PLAN.md section 1.9 originally called for and the
/// previous hardcoded config's own doc comment explicitly noted was skipped to get live faster
/// (2026-09-09, external review -- "the item you skipped to go live"). Plain <see
/// cref="IOptionsMonitor{T}"/> + <see cref="IValidateOptions{T}"/> doesn't actually give this:
/// the built-in validation pipeline throws <see cref="OptionsValidationException"/> on the
/// *next* access after a bad reload, which crashes the reader rather than protecting it, and it
/// has no notion of "keep serving the old value." This wrapper does what the plan actually
/// asked for: bind, map, and validate a new value the moment the underlying file changes; if it
/// fails, log the rejection and go on serving the last value that passed -- the running system
/// never sees a config it hasn't already accepted. The very first value (at construction) is
/// held to the same standard, deliberately un-recoverable: there's no "last known good" yet, so
/// an invalid startup config throws immediately (fail fast, not fail silent) rather than run on
/// a fallback nobody chose.
/// </summary>
public sealed class ValidatedOptionsMonitor<TOptions, TDomain> : IValidatedOptions<TDomain>
    where TOptions : class, new()
{
    readonly Func<TOptions, TDomain> _map;
    readonly Func<TDomain, IReadOnlyList<string>> _validate;
    readonly ILogger<ValidatedOptionsMonitor<TOptions, TDomain>> _logger;
    TDomain _current;

    public ValidatedOptionsMonitor(
        IOptionsMonitor<TOptions> monitor,
        Func<TOptions, TDomain> map,
        Func<TDomain, IReadOnlyList<string>> validate,
        ILogger<ValidatedOptionsMonitor<TOptions, TDomain>> logger)
    {
        _map = map;
        _validate = validate;
        _logger = logger;

        var initial = _map(monitor.CurrentValue);
        var startupErrors = _validate(initial);
        if (startupErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Startup config for {typeof(TDomain).Name} is invalid -- refusing to start rather than run on an unvalidated fallback: {string.Join("; ", startupErrors)}");
        }

        _current = initial;
        monitor.OnChange(OnReload);
    }

    void OnReload(TOptions options)
    {
        TDomain candidate;
        try
        {
            candidate = _map(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config reload for {Type} failed to map -- keeping last-known-good value", typeof(TDomain).Name);
            return;
        }

        var errors = _validate(candidate);
        if (errors.Count > 0)
        {
            _logger.LogError(
                "Rejected config reload for {Type} -- keeping last-known-good value. Errors: {Errors}",
                typeof(TDomain).Name, string.Join("; ", errors));
            return;
        }

        _logger.LogInformation("Applied validated config reload for {Type}", typeof(TDomain).Name);
        _current = candidate;
    }

    public TDomain Current => _current;
}
