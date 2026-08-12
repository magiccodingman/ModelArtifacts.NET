using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelArtifacts;

public sealed class HuggingFaceArtifactSource : IModelArtifactSource
{
    private readonly string repositoryId;
    private readonly string revision;
    private readonly string? accessToken;
    private readonly ArtifactSelection selection;

    public HuggingFaceArtifactSource(string repositoryId, ArtifactSelection selection, string revision = "main", string? accessToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(selection);
        this.repositoryId = repositoryId;
        this.selection = selection;
        this.revision = string.IsNullOrWhiteSpace(revision) ? "main" : revision;
        this.accessToken = accessToken;
    }

    public string SourceKind => "huggingface";
    public string SourceIdentity => repositoryId;
    public string CacheVariant => selection.Identity;

    public async Task<ResolvedArtifactSet> ResolveAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        var escapedRepository = string.Join('/', repositoryId.Split('/').Select(Uri.EscapeDataString));
        var apiUri = new Uri($"https://huggingface.co/api/models/{escapedRepository}?revision={Uri.EscapeDataString(revision)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
        AddAuthorization(request);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ArtifactSourceException($"Hugging Face returned {(int)response.StatusCode} while resolving '{repositoryId}'.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var model = await JsonSerializer.DeserializeAsync(stream, ArtifactJsonContext.Default.HuggingFaceApiModel, cancellationToken).ConfigureAwait(false)
            ?? throw new ArtifactSourceException($"Hugging Face returned an empty model response for '{repositoryId}'.");
        var concreteRevision = string.IsNullOrWhiteSpace(model.Sha) ? revision : model.Sha;
        var assets = (model.Siblings ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.FileName) && selection.Matches(x.FileName!))
            .Select(x => new ArtifactAsset(
                x.FileName!,
                BuildDownloadUri(repositoryId, concreteRevision, x.FileName!),
                x.Size))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();

        if (assets.Length == 0)
            throw new ArtifactSourceException($"Artifact selection '{selection.Identity}' matched no files in Hugging Face repository '{repositoryId}'.");
        return new ResolvedArtifactSet(repositoryId, concreteRevision, assets);
    }

    public void ApplyDownloadHeaders(HttpRequestMessage request) => AddAuthorization(request);

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    internal static Uri BuildDownloadUri(string repositoryId, string revision, string path)
    {
        var repo = string.Join('/', repositoryId.Split('/').Select(Uri.EscapeDataString));
        var rev = Uri.EscapeDataString(revision);
        var asset = string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return new Uri($"https://huggingface.co/{repo}/resolve/{rev}/{asset}?download=true");
    }
}

public sealed class LocalDirectoryArtifactSource : IModelArtifactSource
{
    private readonly string directory;
    private readonly ArtifactSelection selection;

    public LocalDirectoryArtifactSource(string directory, ArtifactSelection? selection = null, string? identity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        this.selection = selection ?? ArtifactSelection.All();
        SourceIdentity = identity ?? this.directory;
    }

    public string SourceKind => "local-directory";
    public string SourceIdentity { get; }
    public string CacheVariant => selection.Identity;

    public Task<ResolvedArtifactSet> ResolveAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        _ = httpClient;
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
            throw new ArtifactSourceException($"Local artifact directory '{directory}' does not exist.");

        var assets = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Where(selection.Matches)
            .Order(StringComparer.Ordinal)
            .Select(path => new ArtifactAsset(path, new Uri(Path.Combine(directory, path))))
            .ToArray();
        if (assets.Length == 0)
            throw new ArtifactSourceException($"Artifact selection '{selection.Identity}' matched no files in local directory '{directory}'.");
        return Task.FromResult(new ResolvedArtifactSet(Path.GetFileName(directory), "local", assets, directory));
    }
}

public sealed class HttpManifestArtifactSource : IModelArtifactSource
{
    private readonly Uri manifestUri;
    private readonly ArtifactSelection? selection;
    private readonly IReadOnlyDictionary<string, string>? headers;

    public HttpManifestArtifactSource(Uri manifestUri, ArtifactSelection? selection = null, IReadOnlyDictionary<string, string>? headers = null, string? identity = null)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (!manifestUri.IsAbsoluteUri) throw new ArgumentException("Manifest URI must be absolute.", nameof(manifestUri));
        this.manifestUri = manifestUri;
        this.selection = selection;
        this.headers = headers;
        SourceIdentity = identity ?? manifestUri.AbsoluteUri;
    }

    public string SourceKind => "http-manifest";
    public string SourceIdentity { get; }
    public string CacheVariant => selection?.Identity ?? "manifest";

    public async Task<ResolvedArtifactSet> ResolveAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        ApplyHeaders(request);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ArtifactSourceException($"HTTP artifact manifest returned {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync(stream, ArtifactJsonContext.Default.HttpManifestDto, cancellationToken).ConfigureAwait(false)
            ?? throw new ArtifactSourceException("HTTP artifact manifest was empty.");
        if (manifest.Assets is null)
            throw new ArtifactSourceException("HTTP artifact manifest must contain an assets array.");

        var assets = new List<ArtifactAsset>();
        foreach (var element in manifest.Assets)
        {
            string path;
            string url;
            long? size = null;
            string? sha256 = null;
            if (element.ValueKind == JsonValueKind.String)
            {
                path = element.GetString() ?? throw new ArtifactSourceException("Manifest asset path is empty.");
                url = path;
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                if (!element.TryGetProperty("path", out var pathElement) || string.IsNullOrWhiteSpace(pathElement.GetString()))
                    throw new ArtifactSourceException("Manifest asset path is empty.");
                path = pathElement.GetString()!;
                url = element.TryGetProperty("url", out var urlElement) && !string.IsNullOrWhiteSpace(urlElement.GetString()) ? urlElement.GetString()! : path;
                if (element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)) size = parsedSize;
                if (element.TryGetProperty("sha256", out var hashElement)) sha256 = hashElement.GetString();
            }
            else
            {
                throw new ArtifactSourceException("Manifest assets must be strings or objects.");
            }
            if (selection is not null && !selection.Matches(path)) continue;
            var assetUri = Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute : new Uri(manifestUri, url);
            assets.Add(new ArtifactAsset(path, assetUri, size, sha256));
        }

        if (assets.Count == 0)
            throw new ArtifactSourceException("HTTP artifact manifest selected no assets.");
        var id = !string.IsNullOrWhiteSpace(manifest.ArtifactSetId) ? manifest.ArtifactSetId! : !string.IsNullOrWhiteSpace(manifest.ModelId) ? manifest.ModelId! : manifestUri.Host;
        var revision = string.IsNullOrWhiteSpace(manifest.Revision) ? "manifest" : manifest.Revision!;
        return new ResolvedArtifactSet(id, revision, assets.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray());
    }

    public void ApplyDownloadHeaders(HttpRequestMessage request) => ApplyHeaders(request);

    private void ApplyHeaders(HttpRequestMessage request)
    {
        if (headers is null) return;
        foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
    }
}

internal sealed class HuggingFaceApiModel
{
    [JsonPropertyName("sha")]
    public string? Sha { get; init; }
    [JsonPropertyName("siblings")]
    public HuggingFaceSibling[]? Siblings { get; init; }
}

internal sealed class HuggingFaceSibling
{
    [JsonPropertyName("rfilename")]
    public string? FileName { get; init; }
    [JsonPropertyName("size")]
    public long? Size { get; init; }
}

internal sealed class HttpManifestDto
{
    [JsonPropertyName("artifactSetId")]
    public string? ArtifactSetId { get; init; }
    [JsonPropertyName("modelId")]
    public string? ModelId { get; init; }
    [JsonPropertyName("revision")]
    public string? Revision { get; init; }
    [JsonPropertyName("assets")]
    public JsonElement[]? Assets { get; init; }
}
