namespace IConnect.Rendezvous;

public sealed class PairingReaper(PairingBroker broker) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken)) broker.PruneExpired();
    }
}
