using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ModelArtifacts;

namespace ModelArtifacts.Tests;

public sealed class ArtifactManagerTests
{
    [Fact]
    public async Task HuggingFace_resolves_concrete_sha()
    {
        var handler = new RouterHandler(req => req.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"sha":"abc123","siblings":[{"rfilename":"model.bin","size":3}]}""")
            : Bytes(HttpStatusCode.OK, "abc"));
        using var manager = Manager(new HuggingFaceArtifactSource("org/repo", ArtifactSelection.Explicit("runtime", "model.bin")), handler);
        var candidate = await manager.ResolveCandidateAsync();
        Assert.Equal("abc123", candidate.Snapshot.SourceRevision);
    }

    [Fact]
    public async Task HuggingFace_downloads_only_selected_assets()
    {
        var requests = new ConcurrentBag<string>();
        var handler = new RouterHandler(req =>
        {
            requests.Add(req.RequestUri!.AbsolutePath);
            return req.RequestUri.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"sha":"sha","siblings":[{"rfilename":"weights/model.bin"},{"rfilename":"README.md"},{"rfilename":"extra.json"}]}""")
                : Bytes(HttpStatusCode.OK, "x");
        });
        using var manager = Manager(new HuggingFaceArtifactSource("org/repo", ArtifactSelection.Patterns("weights", "weights/*")), handler);
        var candidate = await manager.ResolveCandidateAsync();
        Assert.Equal(["weights/model.bin"], candidate.Snapshot.AssetPaths);
        Assert.DoesNotContain(requests, x => x.EndsWith("README.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HuggingFace_readme_is_not_downloaded_unless_selected()
    {
        var handler = HuggingFaceHandler("""{"sha":"sha","siblings":[{"rfilename":"runtime.bin"},{"rfilename":"README.md"}]}""", "data");
        using var manager = Manager(new HuggingFaceArtifactSource("org/repo", ArtifactSelection.Explicit("runtime", "runtime.bin")), handler);
        var candidate = await manager.ResolveCandidateAsync();
        Assert.DoesNotContain("README.md", candidate.Snapshot.AssetPaths);
    }

    [Fact]
    public async Task HuggingFace_authentication_is_attached_to_resolve_and_download()
    {
        var auth = new ConcurrentBag<AuthenticationHeaderValue?>();
        var handler = new RouterHandler(req =>
        {
            auth.Add(req.Headers.Authorization);
            return req.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"sha":"sha","siblings":[{"rfilename":"private.bin"}]}""")
                : Bytes(HttpStatusCode.OK, "secret");
        });
        using var manager = Manager(new HuggingFaceArtifactSource("org/private", ArtifactSelection.All("private"), accessToken: "token"), handler);
        await manager.ResolveCandidateAsync();
        Assert.All(auth, value => Assert.Equal("Bearer token", value?.ToString()));
    }

    [Fact]
    public async Task Http_manifest_supports_relative_assets()
    {
        var handler = new RouterHandler(req => req.RequestUri!.AbsolutePath == "/manifest.json"
            ? Json(HttpStatusCode.OK, """{"artifactSetId":"tiny","revision":"r1","assets":["files/a.bin"]}""")
            : Bytes(HttpStatusCode.OK, "abc"));
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler);
        var candidate = await manager.ResolveCandidateAsync();
        Assert.Equal("abc", await File.ReadAllTextAsync(candidate.Snapshot.GetAssetPath("files/a.bin")));
    }

    [Fact]
    public async Task Http_manifest_supports_explicit_urls()
    {
        Uri? download = null;
        var handler = new RouterHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/manifest.json")
                return Json(HttpStatusCode.OK, """{"artifactSetId":"tiny","revision":"r1","assets":[{"path":"a.bin","url":"https://cdn.test/object"}]}""");
            download = req.RequestUri;
            return Bytes(HttpStatusCode.OK, "abc");
        });
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler);
        await manager.ResolveCandidateAsync();
        Assert.Equal("cdn.test", download!.Host);
    }

    [Fact]
    public async Task Http_manifest_validates_sizes()
    {
        var handler = ManifestHandler("""{"artifactSetId":"tiny","revision":"r1","assets":[{"path":"a.bin","size":3}]}""", "abc");
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler);
        var candidate = await manager.ResolveCandidateAsync();
        Assert.True(File.Exists(candidate.Snapshot.GetAssetPath("a.bin")));
    }

    [Fact]
    public async Task Http_manifest_validates_sha256()
    {
        var hash = Sha256("abc");
        var handler = ManifestHandler($$"""{"artifactSetId":"tiny","revision":"r1","assets":[{"path":"a.bin","sha256":"{{hash}}"}]}""", "abc");
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler);
        var candidate = await manager.ResolveCandidateAsync();
        Assert.True(File.Exists(candidate.Snapshot.GetAssetPath("a.bin")));
    }

    [Fact]
    public async Task Path_traversal_is_rejected_before_download()
    {
        var downloads = 0;
        var handler = new RouterHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/manifest.json") return Json(HttpStatusCode.OK, """{"artifactSetId":"tiny","revision":"r1","assets":[{"path":"../evil.bin","url":"https://cdn.test/evil"}]}""");
            Interlocked.Increment(ref downloads);
            return Bytes(HttpStatusCode.OK, "evil");
        });
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler);
        await Assert.ThrowsAsync<ArtifactDownloadException>(() => manager.ResolveCandidateAsync());
        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task Expected_size_mismatch_fails()
    {
        var handler = ManifestHandler("""{"artifactSetId":"tiny","revision":"r1","assets":[{"path":"a.bin","size":99}]}""", "abc");
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler);
        await Assert.ThrowsAsync<ArtifactDownloadException>(() => manager.ResolveCandidateAsync());
    }

    [Fact]
    public async Task Sha_mismatch_fails()
    {
        var handler = ManifestHandler("""{"artifactSetId":"tiny","revision":"r1","assets":[{"path":"a.bin","sha256":"deadbeef"}]}""", "abc");
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler);
        await Assert.ThrowsAsync<ArtifactDownloadException>(() => manager.ResolveCandidateAsync());
    }

    [Fact]
    public async Task Transient_http_failures_retry()
    {
        var attempts = 0;
        var handler = new RouterHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/manifest.json") return Json(HttpStatusCode.OK, """{"artifactSetId":"tiny","revision":"r1","assets":["a.bin"]}""");
            return Interlocked.Increment(ref attempts) < 3 ? Bytes(HttpStatusCode.ServiceUnavailable, "") : Bytes(HttpStatusCode.OK, "abc");
        });
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler, options => options.DelayOverride = (_, _) => Task.CompletedTask);
        await manager.ResolveCandidateAsync();
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Retry_after_is_respected()
    {
        var observed = new List<TimeSpan>();
        var attempts = 0;
        var handler = new RouterHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/manifest.json") return Json(HttpStatusCode.OK, """{"artifactSetId":"tiny","revision":"r1","assets":["a.bin"]}""");
            if (Interlocked.Increment(ref attempts) == 1)
            {
                var response = Bytes((HttpStatusCode)429, "");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return response;
            }
            return Bytes(HttpStatusCode.OK, "abc");
        });
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler, options => options.DelayOverride = (delay, _) => { observed.Add(delay); return Task.CompletedTask; });
        await manager.ResolveCandidateAsync();
        Assert.Contains(TimeSpan.FromSeconds(7), observed);
    }

    [Fact]
    public async Task Partial_file_never_becomes_active_after_failed_download()
    {
        using var temp = new TempDirectory();
        var handler = ManifestHandler("""{"artifactSetId":"tiny","revision":"r1","assets":[{"path":"a.bin","size":50}]}""", "abc");
        using var manager = Manager(new HttpManifestArtifactSource(new Uri("https://example.test/manifest.json")), handler, cache: temp.Path);
        await Assert.ThrowsAsync<ArtifactDownloadException>(() => manager.ResolveCandidateAsync());
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "current.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Staging_cleanup_removes_abandoned_directory()
    {
        using var temp = new TempDirectory();
        var source = new MutableSource("r1", "abc");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)), cache: temp.Path);
        var first = await manager.ResolveCandidateAsync();
        await manager.PromoteAsync(first, cleanupObsoleteSnapshots: false);
        var abandoned = Path.Combine(first.CacheRoot!, "staging", "abandoned");
        Directory.CreateDirectory(abandoned);
        await File.WriteAllTextAsync(Path.Combine(abandoned, "x.partial"), "x");
        await manager.ResolveCandidateAsync(true);
        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public async Task Current_metadata_is_atomically_promoted()
    {
        var source = new MutableSource("r1", "abc");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)));
        var candidate = await manager.ResolveCandidateAsync();
        await manager.PromoteAsync(candidate);
        Assert.True(File.Exists(Path.Combine(candidate.CacheRoot!, "current.json")));
        Assert.Empty(Directory.EnumerateFiles(candidate.CacheRoot!, "current.json.tmp-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Downloading_candidate_does_not_change_current()
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)));
        var first = await manager.ResolveCandidateAsync();
        await manager.PromoteAsync(first, cleanupObsoleteSnapshots: false);
        source.Revision = "r2"; source.Content = "two";
        var candidate = await manager.RefreshAsync();
        Assert.True(candidate.RequiresPromotion);
        Assert.Equal("r1", (await manager.GetCurrentAsync())!.SourceRevision);
    }

    [Fact]
    public async Task Discarding_candidate_leaves_current_untouched()
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)));
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first, false);
        source.Revision = "r2"; source.Content = "two";
        var candidate = await manager.RefreshAsync();
        await manager.DiscardAsync(candidate);
        Assert.Equal("r1", (await manager.GetCurrentAsync())!.SourceRevision);
        Assert.False(Directory.Exists(candidate.Snapshot.DirectoryPath));
    }

    [Fact]
    public async Task Promoting_candidate_changes_current()
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)));
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first, false);
        source.Revision = "r2"; source.Content = "two";
        var second = await manager.RefreshAsync(); await manager.PromoteAsync(second, false);
        Assert.Equal("r2", (await manager.GetCurrentAsync())!.SourceRevision);
    }

    [Fact]
    public async Task Promotion_cleanup_removes_obsolete_snapshots()
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)));
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first, false);
        var old = first.Snapshot.DirectoryPath;
        source.Revision = "r2"; source.Content = "two";
        var second = await manager.RefreshAsync(); await manager.PromoteAsync(second);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(second.Snapshot.DirectoryPath));
    }

    [Fact]
    public async Task Cleanup_failure_does_not_invalidate_active_snapshot()
    {
        var source = new MutableSource("r1", "one");
        var failDelete = false;
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)), options =>
        {
            options.LockedFileDeleteRetries = 0;
            options.DeleteDirectoryOverride = (path, _) => failDelete ? Task.FromException(new IOException("locked")) : Delete(path);
        });
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first, false);
        source.Revision = "r2"; source.Content = "two";
        var second = await manager.RefreshAsync();
        failDelete = true;
        await manager.PromoteAsync(second, true);
        Assert.Equal("r2", (await manager.GetCurrentAsync())!.SourceRevision);
    }

    [Fact]
    public async Task Same_source_revision_reuses_current_snapshot()
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)));
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first);
        var second = await manager.RefreshAsync();
        Assert.False(second.RequiresPromotion);
        Assert.Equal(first.Snapshot.DirectoryPath, second.Snapshot.DirectoryPath);
    }

    [Fact]
    public async Task Remote_resolution_failure_falls_back_to_current()
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)));
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first);
        source.FailResolution = true;
        var fallback = await manager.RefreshAsync();
        Assert.True(fallback.IsOfflineFallback);
        Assert.Equal("r1", fallback.Snapshot.SourceRevision);
    }

    [Fact]
    public async Task Remote_resolution_failure_without_current_fails_cleanly()
    {
        var source = new MutableSource("r1", "one") { FailResolution = true };
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, "x")));
        await Assert.ThrowsAsync<ArtifactSourceException>(() => manager.ResolveCandidateAsync());
    }

    [Theory]
    [InlineData(ArtifactUpdatePolicy.Never)]
    [InlineData(ArtifactUpdatePolicy.Manual)]
    public async Task Never_and_manual_reuse_current_without_remote_check(ArtifactUpdatePolicy policy)
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)), options => options.UpdatePolicy = policy);
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first);
        var count = source.ResolveCount;
        await manager.ResolveCandidateAsync();
        Assert.Equal(count, source.ResolveCount);
    }

    [Fact]
    public async Task Manual_refresh_forces_remote_check()
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)), options => options.UpdatePolicy = ArtifactUpdatePolicy.Manual);
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first);
        source.Revision = "r2";
        var second = await manager.RefreshAsync();
        Assert.True(second.RequiresPromotion);
    }

    [Fact]
    public async Task OnStartup_checks_remote_revision()
    {
        var source = new MutableSource("r1", "one");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)), options => options.UpdatePolicy = ArtifactUpdatePolicy.OnStartup);
        var first = await manager.ResolveCandidateAsync(); await manager.PromoteAsync(first);
        var count = source.ResolveCount;
        await manager.ResolveCandidateAsync();
        Assert.True(source.ResolveCount > count);
    }

    [Fact]
    public async Task Local_directory_is_used_without_copying()
    {
        using var local = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(local.Path, "weights.gguf"), "abc");
        using var manager = new ArtifactManager(new LocalDirectoryArtifactSource(local.Path));
        var candidate = await manager.ResolveCandidateAsync();
        Assert.False(candidate.RequiresPromotion);
        Assert.False(candidate.Snapshot.IsManaged);
        Assert.Equal(Path.GetFullPath(local.Path), candidate.Snapshot.DirectoryPath);
    }

    [Fact]
    public async Task Artifact_fingerprint_is_deterministic()
    {
        using var local = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(local.Path, "a.bin"), "abc");
        using var manager = new ArtifactManager(new LocalDirectoryArtifactSource(local.Path));
        var a = await manager.ResolveCandidateAsync();
        var b = await manager.ResolveCandidateAsync();
        Assert.Equal(a.Snapshot.ArtifactFingerprint, b.Snapshot.ArtifactFingerprint);
    }

    [Fact]
    public async Task Modifying_managed_artifact_changes_fingerprint()
    {
        using var local = new TempDirectory();
        var path = Path.Combine(local.Path, "a.bin");
        await File.WriteAllTextAsync(path, "abc");
        using var manager = new ArtifactManager(new LocalDirectoryArtifactSource(local.Path));
        var a = await manager.ResolveCandidateAsync();
        await File.WriteAllTextAsync(path, "changed");
        var b = await manager.ResolveCandidateAsync();
        Assert.NotEqual(a.Snapshot.ArtifactFingerprint, b.Snapshot.ArtifactFingerprint);
    }

    [Fact]
    public async Task Different_variants_from_same_source_do_not_collide()
    {
        using var cache = new TempDirectory();
        var handler = HuggingFaceHandler("""{"sha":"same","siblings":[{"rfilename":"int8.bin"},{"rfilename":"fp32.bin"}]}""", "x");
        using var int8 = Manager(new HuggingFaceArtifactSource("org/repo", ArtifactSelection.Explicit("int8", "int8.bin")), handler, cache: cache.Path);
        using var fp32 = Manager(new HuggingFaceArtifactSource("org/repo", ArtifactSelection.Explicit("fp32", "fp32.bin")), handler, cache: cache.Path);
        var a = await int8.ResolveCandidateAsync();
        var b = await fp32.ResolveCandidateAsync();
        Assert.NotEqual(a.CacheRoot, b.CacheRoot);
    }

    [Fact]
    public async Task Cache_lock_prevents_concurrent_corruption()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BlockingSource(entered, gate);
        using var cache = new TempDirectory();
        using var first = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, "abc")), cache: cache.Path);
        using var second = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, "abc")), cache: cache.Path);
        var firstTask = first.ResolveCandidateAsync();
        await entered.Task;
        var secondTask = second.ResolveCandidateAsync();
        await Task.Delay(50);
        Assert.Equal(1, source.ResolveCount);
        gate.SetResult();
        await firstTask;
        await secondTask;
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_cache_lock_works()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BlockingSource(entered, gate);
        using var cache = new TempDirectory();
        using var first = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, "abc")), cache: cache.Path);
        using var second = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, "abc")), cache: cache.Path, options: o => o.LockRetryDelay = TimeSpan.FromMilliseconds(5));
        var firstTask = first.ResolveCandidateAsync();
        await entered.Task;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.ResolveCandidateAsync(cancellationToken: cts.Token));
        gate.SetResult();
        await firstTask;
    }

    [Fact]
    public async Task Custom_source_participates_without_internal_changes()
    {
        var source = new MutableSource("custom-r1", "payload");
        using var manager = Manager(source, new RouterHandler(_ => Bytes(HttpStatusCode.OK, source.Content)));
        var candidate = await manager.ResolveCandidateAsync();
        Assert.Equal("custom", candidate.Snapshot.SourceKind);
        Assert.Equal("custom-r1", candidate.Snapshot.SourceRevision);
    }

    [Fact]
    public async Task Core_has_no_onnx_or_embedding_specific_assumptions()
    {
        using var local = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(local.Path, "weights.gguf"), "model");
        await File.WriteAllTextAsync(Path.Combine(local.Path, "vocab.custom"), "tokens");
        using var manager = new ArtifactManager(new LocalDirectoryArtifactSource(local.Path));
        var candidate = await manager.ResolveCandidateAsync();
        Assert.Contains("weights.gguf", candidate.Snapshot.AssetPaths);
        Assert.Contains("vocab.custom", candidate.Snapshot.AssetPaths);
    }

    private static ArtifactManager Manager(IModelArtifactSource source, HttpMessageHandler handler, Action<ArtifactManagerOptions>? options = null, string? cache = null)
    {
        var temp = cache ?? Path.Combine(Path.GetTempPath(), "ModelArtifacts.Tests", Guid.NewGuid().ToString("N"));
        var settings = new ArtifactManagerOptions { CacheDirectory = temp, LockRetryDelay = TimeSpan.FromMilliseconds(5), LockedFileDeleteRetryDelay = TimeSpan.Zero };
        settings.DelayOverride = (_, _) => Task.CompletedTask;
        options?.Invoke(settings);
        return new ArtifactManager(source, settings, new HttpClient(handler));
    }

    private static RouterHandler HuggingFaceHandler(string apiJson, string download) => new(req => req.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, apiJson) : Bytes(HttpStatusCode.OK, download));
    private static RouterHandler ManifestHandler(string manifest, string download) => new(req => req.RequestUri!.AbsolutePath == "/manifest.json" ? Json(HttpStatusCode.OK, manifest) : Bytes(HttpStatusCode.OK, download));
    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Bytes(HttpStatusCode code, string value) => new(code) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value)) };
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Task Delete(string path) { Directory.Delete(path, true); return Task.CompletedTask; }

    private sealed class RouterHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private class MutableSource(string revision, string content) : IModelArtifactSource
    {
        public string Revision { get; set; } = revision;
        public string Content { get; set; } = content;
        public bool FailResolution { get; set; }
        public int ResolveCount { get; private set; }
        public virtual string SourceKind => "custom";
        public virtual string SourceIdentity => "test-source";
        public virtual string CacheVariant => "runtime";
        public virtual Task<ResolvedArtifactSet> ResolveAsync(HttpClient httpClient, CancellationToken cancellationToken)
        {
            _ = httpClient;
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCount++;
            if (FailResolution) throw new ArtifactSourceException("offline");
            return Task.FromResult(new ResolvedArtifactSet("test", Revision, [new ArtifactAsset("artifact.any", new Uri("https://cdn.test/artifact"), Encoding.UTF8.GetByteCount(Content))]));
        }
    }

    private sealed class BlockingSource(TaskCompletionSource entered, TaskCompletionSource gate) : IModelArtifactSource
    {
        private int count;
        public int ResolveCount => count;
        public string SourceKind => "blocking";
        public string SourceIdentity => "same";
        public string CacheVariant => "same";
        public async Task<ResolvedArtifactSet> ResolveAsync(HttpClient httpClient, CancellationToken cancellationToken)
        {
            _ = httpClient;
            Interlocked.Increment(ref count);
            entered.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            return new ResolvedArtifactSet("blocking", "r1", [new ArtifactAsset("a.bin", new Uri("https://cdn.test/a.bin"), 3)]);
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
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
