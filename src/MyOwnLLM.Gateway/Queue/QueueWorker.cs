namespace MyOwnLLM.Gateway.Queue;

public sealed class QueueWorker(RequestQueue queue) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => queue.RunAsync(stoppingToken);
}
