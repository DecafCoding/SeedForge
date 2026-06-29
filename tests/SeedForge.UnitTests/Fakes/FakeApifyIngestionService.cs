using SeedForge.Services.Apify;

namespace SeedForge.UnitTests.Fakes
{
    /// <summary>
    /// In-memory <see cref="IApifyIngestionService"/> for slice/runner tests. Either returns a canned
    /// <see cref="IngestedVideo"/> or throws a configured <see cref="ApifyException"/>; never touches the network.
    /// Records how many times it was called so tests can assert idempotency (no second fetch).
    /// </summary>
    public sealed class FakeApifyIngestionService : IApifyIngestionService
    {
        private readonly Func<string, IngestedVideo> _factory;
        private readonly ApifyException? _throw;

        public int CallCount { get; private set; }

        private FakeApifyIngestionService(Func<string, IngestedVideo> factory, ApifyException? toThrow)
        {
            _factory = factory;
            _throw = toThrow;
        }

        public static FakeApifyIngestionService Returns(IngestedVideo result) => new(_ => result, null);

        public static FakeApifyIngestionService WithTranscript(
            string videoId, string text, string? title = "Title", string? channel = "Channel", double? cost = null) =>
            new(_ => new IngestedVideo(true, text, title, channel, $$"""{"title":"{{title}}"}""", cost, videoId), null);

        public static FakeApifyIngestionService NoTranscript(string videoId, string? title = "Title") =>
            new(_ => new IngestedVideo(false, null, title, null, """{}""", null, videoId), null);

        public static FakeApifyIngestionService Throws(string message = "boom") =>
            new(_ => throw new InvalidOperationException("unreachable"), new ApifyException(message, statusCode: 500));

        public Task<IngestedVideo> FetchAsync(string urlOrId, CancellationToken ct = default)
        {
            CallCount++;
            if (_throw is not null) throw _throw;
            return Task.FromResult(_factory(urlOrId));
        }
    }
}
