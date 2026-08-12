using ModelArtifacts;
using Xunit;

namespace ModelArtifacts.Tests;

public sealed class SelectionIdentityTests
{
    [Fact]
    public void Different_paths_do_not_collide_even_when_variant_name_is_reused()
    {
        var int8 = new HuggingFaceArtifactSource(
            "org/repo",
            ArtifactSelection.Explicit("runtime", "exports/int8/model.bin"));
        var fp32 = new HuggingFaceArtifactSource(
            "org/repo",
            ArtifactSelection.Explicit("runtime", "exports/fp32/model.bin"));

        Assert.NotEqual(int8.CacheVariant, fp32.CacheVariant);
    }

    [Fact]
    public void Equivalent_selections_have_stable_identity_regardless_of_input_order()
    {
        var first = ArtifactSelection.Explicit("runtime", "b.bin", "a.bin");
        var second = ArtifactSelection.Explicit("runtime", "a.bin", "b.bin");

        Assert.Equal(first.Identity, second.Identity);
    }
}
