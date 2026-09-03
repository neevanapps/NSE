namespace NiftySignal.Domain.Enums;

/// <summary>
/// None applies to non-option instruments (index, future) -- keeping this on the enum
/// rather than a nullable OptionType? on Instrument avoids a null that means two
/// different things (not-an-option vs not-yet-known).
/// </summary>
public enum OptionType
{
    None,
    Call,
    Put,
}
