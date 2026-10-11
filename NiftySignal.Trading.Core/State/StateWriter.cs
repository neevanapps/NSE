using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NiftySignal.Trading.Core.State;

/// <summary>
/// A component that owns mutable state a recovery check must see (contract section A.18.3). Strategies with their own state implement it so the
/// state digest covers them; a stateless strategy needs nothing.
/// </summary>
public interface IStateContributor
{
    void WriteState(StateWriter writer);
}

/// <summary>Formatting helpers for nullable values inside state lines.</summary>
public static class StateFormat
{
    public static string Dec(this decimal? value) => value is null ? "~" : StateWriter.Dec(value.Value);

    public static string Ticks(this DateTimeOffset? value) => value is null ? "~" : value.Value.UtcTicks.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Canonical, culture-independent text of the behaviour-relevant mutable state, one <c>path=value</c> line each, in a deterministic order, plus its
/// SHA-256. The text is kept (not just the hash) so a mismatch can be reported at the first differing line.
/// <para>
/// Scope. Included: everything that can change what the engine does or outputs next — clock, deadline queue with creation sequences, opportunities,
/// reservations, orders, positions, TP1, per-order charge state, consumed liquidity, quotes, candles, integrity and kill state. Excluded on purpose:
/// run labels (<c>RunId</c>, <c>RunMode</c>), wall-clock measurements, transport diagnostics, input that has been offered but not yet released,
/// the loop's duplicate-detection memory (retention dependent) and counters that merely describe transport.
/// </para>
/// </summary>
public sealed class StateWriter
{
    /// <summary>Version of the state-text layout. Changing any line, order or format requires a new version.</summary>
    public const string Version = "trading-state/1";

    private readonly StringBuilder _text = new();
    private readonly Stack<string> _scopes = new();

    public StateWriter()
    {
        _text.Append("#version=").Append(Version).Append('\n');
    }

    public string Text => _text.ToString();

    public string Digest => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));

    public IDisposable Scope(string name)
    {
        _scopes.Push(name);
        return new ScopeExit(this);
    }

    public void Line(string path, string? value)
    {
        foreach (var scope in _scopes.Reverse())
        {
            _text.Append(scope).Append('/');
        }

        _text.Append(path).Append('=').Append(value ?? "~").Append('\n');
    }

    public void Line(string path, long value) => Line(path, value.ToString(CultureInfo.InvariantCulture));

    public void Line(string path, long? value) => Line(path, value is null ? null : value.Value.ToString(CultureInfo.InvariantCulture));

    public void Line(string path, bool value) => Line(path, value ? "1" : "0");

    public void Line(string path, decimal value) => Line(path, Dec(value));

    public void Line(string path, decimal? value) => Line(path, value is null ? null : Dec(value.Value));

    public void Line(string path, DateTimeOffset value) => Line(path, value.UtcTicks.ToString(CultureInfo.InvariantCulture));

    public void Line(string path, DateTimeOffset? value) => Line(path, value is null ? null : value.Value.UtcTicks.ToString(CultureInfo.InvariantCulture));

    public void Line(string path, TimeSpan? value) => Line(path, value is null ? null : value.Value.Ticks.ToString(CultureInfo.InvariantCulture));

    public void Line(string path, double value) => Line(path, value.ToString("R", CultureInfo.InvariantCulture));

    public void Line(string path, double? value) => Line(path, value is null ? null : value.Value.ToString("R", CultureInfo.InvariantCulture));

    public void Line<TEnum>(string path, TEnum value)
        where TEnum : struct, Enum
        => Line(path, value.ToString());

    public void Line<TEnum>(string path, TEnum? value)
        where TEnum : struct, Enum
        => Line(path, value?.ToString());

    /// <summary>Scale-preserving decimal text (125.00 and 125.0 are different state), with the sign of a negative zero kept.</summary>
    public static string Dec(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return value == 0m && (decimal.GetBits(value)[3] & int.MinValue) != 0 && !text.StartsWith('-') ? "-" + text : text;
    }

    private sealed class ScopeExit(StateWriter owner) : IDisposable
    {
        public void Dispose() => owner._scopes.Pop();
    }
}
