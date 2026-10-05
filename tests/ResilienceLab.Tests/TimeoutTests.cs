using Microsoft.Extensions.Time.Testing;
using TimeoutPolicy = ResilienceLab.Timeout;

namespace ResilienceLab.Tests;

public class TimeoutTests
{
    [Fact]
    public async Task SuccessfulOperationReturnsItsValueAndReleasesTheTimer()
    {
        var clock = new FakeTimeProvider();
        CancellationToken operationToken = default;
        var result = await TimeoutPolicy.ExecuteAsync(token =>
        {
            operationToken = token;
            return Task.FromResult(42);
        }, TimeSpan.FromSeconds(1), clock);

        Assert.Equal(42, result);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(operationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ErrorBeforeTheDeadlinePreservesTheOriginalException()
    {
        var error = new HttpRequestException();
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => TimeoutPolicy.ExecuteAsync<int>(
            _ => Task.FromException<int>(error), TimeSpan.FromSeconds(1), new FakeTimeProvider()));

        Assert.Same(error, thrown);
    }

    [Fact]
    public async Task DeadlineCancelsTheOperationAndReportsTimeout()
    {
        var clock = new FakeTimeProvider();
        CancellationToken operationToken = default;
        var task = TimeoutPolicy.ExecuteAsync(async token =>
        {
            operationToken = token;
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return 42;
        }, TimeSpan.FromSeconds(1), clock);

        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(operationToken.IsCancellationRequested);
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.True(operationToken.IsCancellationRequested);
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConsumerCancellationIsPropagatedBeforeTheDeadline()
    {
        using var source = new CancellationTokenSource();
        var task = TimeoutPolicy.ExecuteAsync(async token =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return 42;
        }, TimeSpan.FromSeconds(10), new FakeTimeProvider(), source.Token);

        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PreCanceledConsumerTokenPreventsTheOperation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TimeoutPolicy.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromResult(42);
        }, TimeSpan.FromSeconds(1), new FakeTimeProvider(), source.Token));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task OperationIgnoringCancellationIsAwaitedBeforeReportingTimeout()
    {
        var clock = new FakeTimeProvider();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;
        var task = TimeoutPolicy.ExecuteAsync(token =>
        {
            operationToken = token;
            return completion.Task;
        }, TimeSpan.FromSeconds(1), clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(operationToken.IsCancellationRequested);
        Assert.False(task.IsCompleted);
        completion.SetResult(42);

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConsumerCancellationTakesPriorityWhenBothCancellationSourcesHaveFired()
    {
        using var source = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = TimeoutPolicy.ExecuteAsync(_ => completion.Task, TimeSpan.FromSeconds(1), clock, source.Token);

        clock.Advance(TimeSpan.FromSeconds(1));
        source.Cancel();
        completion.SetResult(42);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ErrorAfterTheDeadlineIsReportedAsTimeout()
    {
        var clock = new FakeTimeProvider();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = TimeoutPolicy.ExecuteAsync(_ => completion.Task, TimeSpan.FromSeconds(1), clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        completion.SetException(new HttpRequestException());

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295d)]
    public async Task UnsupportedTimeoutsFailBeforeCallingTheOperation(double milliseconds)
    {
        var attempts = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TimeoutPolicy.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromResult(42);
        }, TimeSpan.FromMilliseconds(milliseconds), new FakeTimeProvider()));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task LargestSupportedTimeoutIsAccepted()
    {
        Assert.Equal(42, await TimeoutPolicy.ExecuteAsync(_ => Task.FromResult(42),
            TimeSpan.FromMilliseconds(uint.MaxValue - 1), new FakeTimeProvider()));
    }
}
