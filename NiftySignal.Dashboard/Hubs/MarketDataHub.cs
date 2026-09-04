using Microsoft.AspNetCore.SignalR;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Dashboard.Hubs;

/// <summary>
/// The Host-to-Dashboard push channel (plan-adjacent decision, 2026-09-04): Host is the
/// only SignalR *client* here -- browsers never connect to this hub directly. Blazor
/// Server components already get pushed to the browser over the framework's own
/// connection the moment <see cref="LiveDataService"/> raises its update events, so this
/// hub's only job is "receive a tick from Host, hand it to LiveDataService, done." Ticks
/// no longer have to round-trip through Postgres and a poll timer to reach the Live Quote
/// panel.
/// </summary>
public sealed class MarketDataHub(LiveDataService liveData) : Hub
{
    public void PushTick(Tick tick) => liveData.ApplyPushedTick(tick);
}
