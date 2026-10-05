using Microsoft.Extensions.Time.Testing;

namespace ResilienceLab.Tests;

public class CompositionTests
{
    [Fact]
    public async Task CircuitCountsExhaustedOperationsRatherThanIndividualRetryAttempts()
    {
        var clock = new FakeTimeProvider();
        var circuit = new CircuitBreaker(new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            ShouldHandle = ex => ex is HttpRequestException or TimeoutRejectedException
        }, clock);
        var attempts = 0;
        var retry = new RetryOptions
        {
            MaxRetries = 2, BaseDelay = TimeSpan.Zero,
            ShouldRetry = ex => ex is HttpRequestException or TimeoutRejectedException
        };
        Task<int> Execute() => circuit.ExecuteAsync(ct => Retry.ExecuteAsync(
            retryToken => ResilienceLab.Timeout.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new HttpRequestException();
            }, TimeSpan.FromSeconds(1), clock, retryToken), retry, ct));

        await Assert.ThrowsAsync<HttpRequestException>(Execute);
        Assert.Equal(3, attempts);
        Assert.Equal(CircuitState.Closed, circuit.State);
        await Assert.ThrowsAsync<HttpRequestException>(Execute);
        Assert.Equal(6, attempts);
        Assert.Equal(CircuitState.Open, circuit.State);
        await Assert.ThrowsAsync<BrokenCircuitException>(Execute);
        Assert.Equal(6, attempts);
    }
}
