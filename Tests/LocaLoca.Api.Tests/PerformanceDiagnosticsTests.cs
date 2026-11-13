using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using LocaLoca.Api;
using Xunit;

namespace LocaLoca.Api.Tests;

[Collection(PostgreSqlApiCollection.Name)]
public sealed class PerformanceDiagnosticsTests(PostgreSqlApiFixture fixture)
{
    [Fact]
    public async Task Http_and_postgresql_command_durations_are_recorded()
    {
        var measurements = new ConcurrentDictionary<string, ConcurrentBag<double>>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PerformanceDiagnosticsExtensions.MeterName &&
                    instrument.Name is PerformanceDiagnosticsExtensions.HttpRequestDurationName
                        or PerformanceDiagnosticsExtensions.DbCommandDurationName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            measurements.GetOrAdd(instrument.Name, _ => new ConcurrentBag<double>())
                .Add(measurement);
        });
        listener.Start();

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            measurements.TryGetValue(PerformanceDiagnosticsExtensions.HttpRequestDurationName, out var httpDurations) &&
            httpDurations.Any(duration => duration >= 0),
            "The HTTP request histogram did not record a measurement.");
        Assert.True(
            measurements.TryGetValue(PerformanceDiagnosticsExtensions.DbCommandDurationName, out var dbDurations) &&
            dbDurations.Any(duration => duration >= 0),
            "The PostgreSQL command histogram did not record a measurement.");
    }

    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("post", "POST")]
    [InlineData("PATCH", "PATCH")]
    [InlineData("CUSTOM", "OTHER")]
    [InlineData("CUSTOM\r\nInjected: true", "OTHER")]
    [InlineData("", "OTHER")]
    public void Http_method_is_reduced_to_a_safe_standard_value(string input, string expected)
    {
        Assert.Equal(expected, PerformanceDiagnosticsExtensions.NormalizeHttpMethod(input));
    }
}
