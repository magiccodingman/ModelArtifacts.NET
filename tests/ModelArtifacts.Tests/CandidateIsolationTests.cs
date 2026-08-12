using System.Net;
using System.Text;
using ModelArtifacts;
using Xunit;

namespace ModelArtifacts.Tests;

public sealed class CandidateIsolationTests
{
    [Fact]
    public async Task Same_revision_candidates_use_distinct_snapshot_directories()
    {
        using var cache = new TempDirectory();
        using var first = CreateManager(cache.Path);
        using var second = CreateManager(cache.Path);

        var firstCandidate = await first.ResolveCandidateAsync();
        var secondCandidate = await second.ResolveCandidateAsync();

        Assert.NotEqual(firstCandidate.Snapshot.DirectoryPath, secondCandidate.Snapshot.DirectoryPath);
        Assert.True(File.Exists(firstCandidate.Snapshot.GetAssetPath("artifact.bin")));
        Assert.True(File.Exists(secondCandidate.Snapshot.GetAssetPath("artifact.bin")));

        await first.DiscardAsync(firstCandidate);
        await second.DiscardAsync(secondCandidate);
    }

    [Fact]
    public async Task Promoting_one_candidate_does_not_cleanup_another_candidate_under_validation()
    {
        using var cache = new TempDirectory();
        using var first = CreateManager(cache.Path);
        using var second = CreateManager(cache.Path);

        var firstCandidate = await first.ResolveCandidateAsync();
        var secondCandidate = await second.ResolveCandidateAsync();

        await first.PromoteAsync(firstCandidate, cleanupObsoleteSnapshots: true);

        Assert.True(Directory.Exists(firstCandidate.Snapshot.DirectoryPath));
        Assert.True(Directory.Exists(secondCandidate.Snapshot.DirectoryPath));
        Assert.Equal("r1", (await first.GetCurrentAsync())!.SourceRevision);

        await second.DiscardAsync(secondCandidate);
    }

    private static ArtifactManager CreateManager(string cacheDirectory)
    {
        var options = new ArtifactManagerOptions
        {
            CacheDirectory = cacheDirectory,
            LockRetryDelay = TimeSpan.FromMilliseconds(5),
            LockedFileDeleteRetryDelay = TimeSpan.Zero
        };
        options.DelayOverride = (_, _) => Task.CompletedTask;
        return new ArtifactManager(
            new StaticSource(),
            options,
            new HttpClient(new StaticHandler()));
    }

    private sealed class StaticSource : IModelArtifactSource
    {
        public string SourceKind => "candidate-isolation-test";
        public string SourceIdentity => "shared-source";
        public string CacheVariant => "same-selection";

        public Task<ResolvedArtifactSet> ResolveAsync(HttpClient httpClient, CancellationToken cancellationToken)
        {
            _ = httpClient;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ResolvedArtifactSet(
                "test-artifacts",
                "r1",
                [new ArtifactAsset("artifact.bin", new Uri("https://example.test/artifact.bin"), 3)]));
        }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc"))
            });
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModelArtifacts.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
