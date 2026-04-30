public interface IFeedScanner
{
    string SiteName { get; }
    Task<List<Product>> ScanAsync(string collectionUrl, CancellationToken ct = default);
}
