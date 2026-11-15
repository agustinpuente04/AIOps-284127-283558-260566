using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using System.Diagnostics.Metrics;

public class TestMetricService : BackgroundService
{
    private readonly Counter<long> _testCounter;
    private readonly Meter _meter = new Meter("PharmaGo.CustomMetrics");

    public TestMetricService()
    {
        _testCounter = _meter.CreateCounter<long>("test_counter");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _testCounter.Add(1);
            await Task.Delay(5000, stoppingToken);
        }
    }
}
