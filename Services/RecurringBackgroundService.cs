namespace ExpenseTracker.Services;

/// <summary>
/// Materialises due recurring rules while the app is running. It also runs once
/// at startup, which is what makes a machine that was switched off over the 1st
/// of the month come back with the rent already booked.
/// </summary>
public class RecurringBackgroundService(
    IServiceProvider services,
    ILogger<RecurringBackgroundService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = Interval;

            try
            {
                using var scope = services.CreateScope();
                var recurring = scope.ServiceProvider.GetRequiredService<RecurringService>();
                await recurring.GenerateDueAsync(DateOnly.FromDateTime(DateTime.Now));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Düzenli işlem üretimi başarısız.");
                wait = RetryAfterFailure;
            }

            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
