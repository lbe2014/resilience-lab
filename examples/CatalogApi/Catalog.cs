using System.Net;
using System.Text.Json;
using ResilienceLab;
using ResilienceLab.Http;

namespace CatalogApi;

public sealed record Product(int Id, string Title, decimal Price);
public sealed record ProductPage(Product[] Products, int Total);
public sealed record CatalogSnapshot(IReadOnlyList<Product> Products, int Total,
    DateTimeOffset FetchedAt, bool IsStale);

// A single bounded snapshot for the fixed catalog query. Never cache errors.
public sealed class CatalogCache(TimeProvider clock)
{
    private readonly object gate = new();
    private CatalogSnapshot? snapshot;
    private long savedAt;

    public void Store(CatalogSnapshot value)
    {
        lock (gate) { snapshot = value; savedAt = clock.GetTimestamp(); }
    }

    public CatalogSnapshot? Read(TimeSpan maximumAge)
    {
        lock (gate)
        {
            return snapshot is not null && clock.GetElapsedTime(savedAt) <= maximumAge
                ? snapshot with { IsStale = true } : null;
        }
    }
}

public static class CatalogFailures
{
    public static bool IsTransient(Exception error) => error is TimeoutRejectedException
        || error is HttpRequestException request && (request.StatusCode is null
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout);
}

public sealed class CatalogService(ResilientHttpClient client, CatalogCache cache, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CatalogSnapshot> FetchAsync(CancellationToken token)
    {
        using var response = await client.SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            "products?limit=10&select=id,title,price"), token);
        response.EnsureSuccessStatusCode();
        // Read and dispose inside the timeout. Return DTOs, never live HTTP resources.
        await response.Content.LoadIntoBufferAsync(1024 * 1024, token);
        var page = await response.Content.ReadFromJsonAsync<ProductPage>(JsonOptions, token);
        if (page is null || page.Products is null || page.Products.Length > 10 || page.Total < page.Products.Length
            || page.Products.Any(product => product is null || product.Id <= 0
                || string.IsNullOrWhiteSpace(product.Title) || product.Price < 0))
            throw new JsonException("Invalid catalog contract.");
        token.ThrowIfCancellationRequested();
        var result = new CatalogSnapshot(Array.AsReadOnly(page.Products), page.Total,
            clock.GetUtcNow(), IsStale: false);
        cache.Store(result);
        return result;
    }
}
