using System.Text;
using System.Text.Json;
using Clinic.Application.Notebook;
using Clinic.Application.Storage;

namespace Clinic.UnitTests;

public sealed class NotebookPayloadTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"formatVersion\":\"1\"}")]
    public void InvalidPayloadIsRejected(string json) =>
        Assert.False(NotebookPayloadService.ValidPayload(Encoding.UTF8.GetBytes(json), Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public void ContractRequiresExactVersionOwnershipAndFields()
    {
        var patient = Guid.NewGuid(); var page = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { formatVersion = 1, patientId = patient, pageId = page });
        Assert.True(NotebookPayloadService.ValidPayload(Encoding.UTF8.GetBytes(json), patient, page));
        Assert.False(NotebookPayloadService.ValidPayload(Encoding.UTF8.GetBytes(json), Guid.NewGuid(), page));
        Assert.False(NotebookPayloadService.ValidPayload(Encoding.UTF8.GetBytes(json), patient, Guid.NewGuid()));
        Assert.False(NotebookPayloadService.ValidPayload(Encoding.UTF8.GetBytes(json.Replace("\"formatVersion\":1", "\"formatVersion\":2")), patient, page));
        Assert.False(NotebookPayloadService.ValidPayload(Encoding.UTF8.GetBytes(json[..^1] + ",\"strokes\":[]}"), patient, page));
        Assert.False(NotebookPayloadService.ValidPayload(Encoding.UTF8.GetBytes(json[..^1] + ",\"formatVersion\":1}"), patient, page));
    }

    [Fact]
    public async Task ByteLimitUsesActualBytes()
    {
        using var input = new MemoryStream(new byte[NotebookPayloadService.MaxBytes + 1]);
        await Assert.ThrowsAsync<FileSizeLimitExceededException>(() => NotebookPayloadService.ReadBoundedAsync(input, default));
    }
}
