using ModelArtifacts;

var directory = Path.Combine(AppContext.BaseDirectory, "fixture");
Directory.CreateDirectory(directory);
await File.WriteAllTextAsync(Path.Combine(directory, "tiny.txt"), "tiny artifact fixture");
using var manager = new ArtifactManager(new LocalDirectoryArtifactSource(directory));
var candidate = await manager.ResolveCandidateAsync();
if (candidate.RequiresPromotion || candidate.Snapshot.AssetPaths.Count != 1 || string.IsNullOrWhiteSpace(candidate.Snapshot.ArtifactFingerprint))
    throw new InvalidOperationException("Native AOT managed smoke failed.");
Console.WriteLine("ModelArtifacts.NET Native AOT managed smoke passed");
