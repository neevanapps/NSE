using Microsoft.Extensions.DependencyInjection;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Ingestion.Upstox;

namespace NiftySignal.Ingestion;

public static class MarketDataRegistration
{
    public static IServiceCollection AddMarketDataProviders(this IServiceCollection services)
    {
        services.AddHttpClient<FlatTradeAuthClient>();
        services.AddHttpClient<FlatTradeInstrumentMasterProvider>();
        services.AddScoped<IInstrumentMasterProvider>(sp => sp.GetRequiredService<FlatTradeInstrumentMasterProvider>());
        services.AddScoped<InstrumentUniverseResolver>();
        services.AddHttpClient<SensexBankNiftyInstrumentMasterProvider>();
        services.AddScoped<SensexBankNiftyInstrumentUniverseResolver>();
        services.AddScoped<IMarketDataProvider, FlatTradeMarketDataProvider>();
        services.AddHttpClient<UpstoxAuthClient>();
        services.AddHttpClient<UpstoxRestClient>();
        services.AddHttpClient<UpstoxInstrumentMasterProvider>();
        services.AddScoped<IMarketDataProvider, UpstoxMarketDataProvider>();
        return services;
    }
}
