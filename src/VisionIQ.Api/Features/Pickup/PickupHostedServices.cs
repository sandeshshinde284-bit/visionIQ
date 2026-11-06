namespace VisionIQ.Api.Features.Pickup;

/// <summary>Runs <see cref="PickupProcessor"/> continuously: pickups become runs within seconds of being stored.</summary>
public sealed class PickupWorker(
    PickupProcessor processor, PickupOptions options, ILogger<PickupWorker> logger, bool sweepEnabled)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PickupLog.Enabled(
            logger, options.Policy.Revision, options.Policy.Projects.Count, sweepEnabled ? options.SweepInterval.TotalMinutes : 0);
        if (!sweepEnabled)
        {
            PickupLog.SweepOff(logger);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await processor.ProcessPendingAsync(cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                // Type only: provider messages can include connection details.
                PickupLog.WorkerFailed(logger, error.GetType().Name);
            }

            if (claimed == 0 && !await DelayAsync(options.WorkerInterval, stoppingToken))
            {
                break;
            }
        }
    }

    internal static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Runs <see cref="PickupSweep"/> shortly after start-up and then every PICKUP_SWEEP_INTERVAL_MINUTES.</summary>
public sealed class PickupSweepService(PickupSweep sweep, PickupOptions options, ILogger<PickupSweepService> logger)
    : BackgroundService
{
    private static readonly TimeSpan FirstSweepDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await PickupWorker.DelayAsync(FirstSweepDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sweep.SweepAsync(options.SweepLookback, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                PickupLog.WorkerFailed(logger, error.GetType().Name);
            }

            if (!await PickupWorker.DelayAsync(options.SweepInterval, stoppingToken))
            {
                break;
            }
        }
    }
}
