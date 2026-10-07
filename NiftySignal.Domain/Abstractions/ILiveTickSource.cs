using NiftySignal.Domain.Enums;

namespace NiftySignal.Domain.Abstractions;

public interface ILiveTickSource : ITickSource
{
    void RequestSubscribe(Exchange exchange, string token);
    event Func<int, Task>? ConnectionUnstable;
}
