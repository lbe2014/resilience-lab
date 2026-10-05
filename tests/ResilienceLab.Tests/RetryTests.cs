using ResilienceLab;
namespace ResilienceLab.Tests;
public class RetryTests
{
    private static RetryOptions Options(int retries = 3) => new()
    { MaxRetries = retries, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero,
      ShouldRetry = ex => ex is HttpRequestException };

    [Fact] public async Task RecoversAfterTwoFailures()
    {
        var attempts = 0;
        var result = await Retry.ExecuteAsync(_ => ++attempts < 3
            ? Task.FromException<int>(new HttpRequestException()) : Task.FromResult(42), Options());
        Assert.Equal(42, result); Assert.Equal(3, attempts);
    }
    [Theory] [InlineData(0)] [InlineData(3)]
    public async Task ExhaustionPreservesExceptionAndAttemptCount(int retries)
    {
        var attempts = 0; var error = new HttpRequestException();
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => Retry.ExecuteAsync<int>(_ =>
        { attempts++; throw error; }, Options(retries)));
        Assert.Same(error, thrown); Assert.Equal(retries + 1, attempts);
    }
    [Fact] public async Task DoesNotRetryUnselectedErrors()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Retry.ExecuteAsync<int>(_ =>
        { attempts++; throw new InvalidOperationException(); }, Options()));
        Assert.Equal(1, attempts);
    }
    [Fact] public async Task NeverRetriesCancellationEvenWithBroadFilter()
    {
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry.ExecuteAsync<int>(_ =>
        { attempts++; throw new OperationCanceledException(); }, Options() with { ShouldRetry = _ => true }));
        Assert.Equal(1, attempts);
    }
    [Fact] public async Task PreCanceledTokenDoesNotInvokeOperation()
    {
        using var source = new CancellationTokenSource(); source.Cancel(); var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retry.ExecuteAsync(_ =>
        { attempts++; return Task.FromResult(1); }, Options(), source.Token));
        Assert.Equal(0, attempts);
    }
    [Fact] public async Task CancellationInterruptsBackoff()
    {
        using var source = new CancellationTokenSource();
        var enteredFilter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var task = Retry.ExecuteAsync<int>(_ => { attempts++; throw new HttpRequestException(); },
            Options() with { BaseDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30),
                UseJitter = false, ShouldRetry = _ => { enteredFilter.SetResult(); return true; } }, source.Token);
        await enteredFilter.Task; source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, attempts);
    }
    [Fact] public async Task RejectsInvalidOptionsBeforeCallingOperation()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Retry.ExecuteAsync(_ => Task.FromResult(1), Options(-1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Retry.ExecuteAsync(_ => Task.FromResult(1),
            Options() with { BaseDelay = TimeSpan.FromSeconds(1) }));
    }
}
