using Clinic.Application.Storage;
namespace Clinic.UnitTests;

public sealed class FileStoragePublicRootGateTests
{
    [Theory]
    [InlineData("wwwroot", false)]
    [InlineData("wwwroot/", false)]
    [InlineData("wwwroot/files", false)]
    [InlineData("wwwroot/../wwwroot/files", false)]
    [InlineData("wwwroot-private", true)]
    [InlineData("data/files", true)]
    public void PublicRootItselfAndDescendantsAreRejected(string local, bool allowed)
    {
        var parent = Path.GetTempPath();
        var options = new FileStorageOptions { LocalRoot = Path.Combine(parent, local) };
        Assert.Equal(allowed, options.IsOutsideWebRoot(Path.Combine(parent, "wwwroot")));
    }
}
