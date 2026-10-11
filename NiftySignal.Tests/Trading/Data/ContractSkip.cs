using Xunit.Abstractions;
using Xunit.Sdk;

namespace NiftySignal.Tests.Trading.Data;

// The store contract is written once in a base class and run against every store. xunit 2 cannot skip per concrete class, so this attribute decides at discovery time,
// from the name of the concrete class, whether a test runs: the PostgreSQL contract classes are skipped (visibly, with a reason) while the local test database is not
// configured; every other store always runs.

[XunitTestCaseDiscoverer("NiftySignal.Tests.Trading.Data.ContractFactDiscoverer", "NiftySignal.Tests")]
public sealed class ContractFactAttribute : FactAttribute
{
}

internal static class ContractSkip
{
    public static string? ReasonFor(ITestMethod method)
        => method.TestClass.Class.Name.Contains("Postgres", StringComparison.Ordinal) && !PostgresTestDatabase.Available
            ? $"{PostgresTestDatabase.Variable} is not set: the local PostgreSQL contract tests are skipped."
            : null;
}

public sealed class ContractFactDiscoverer(IMessageSink sink) : FactDiscoverer(sink)
{
    private readonly IMessageSink _sink = sink;

    protected override IXunitTestCase CreateTestCase(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
        => new ContractTestCase(_sink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
}

public sealed class ContractTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes", error: true)]
    public ContractTestCase()
    {
    }

    public ContractTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }

    protected override void Initialize()
    {
        base.Initialize();
        if (ContractSkip.ReasonFor(TestMethod) is { } reason)
        {
            SkipReason = reason;
        }
    }
}
