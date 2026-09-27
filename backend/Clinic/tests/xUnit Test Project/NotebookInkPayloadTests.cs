using Clinic.Application.Notebook;
using MessagePack;

namespace Clinic.UnitTests;

public sealed class NotebookInkPayloadTests
{
    private static readonly Guid Patient = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Page = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static Dictionary<string, object?> Stroke(object? x = null, object? pressure = null, long id = 0) => new()
    {
        ["id"] = id, ["color"] = 0xff000000L, ["width"] = .7,
        ["points"] = new object[] { new object?[] { x ?? 20d, 30d, pressure, 0L }, new object?[] { 40d, 60d, .8, 10000L } }
    };
    private static Dictionary<string, object?> Payload() => new()
    {
        ["formatVersion"] = 2, ["patientId"] = Patient.ToString(), ["pageId"] = Page.ToString(),
        ["pageWidthUnits"] = 210d, ["pageHeightUnits"] = 297d, ["strokes"] = new object[] { Stroke() }
    };
    private static byte[] Pack(object value) => MessagePackSerializer.Serialize(value);
    [Fact]
    public void RealInkAndEmptyDocumentAreValid()
    {
        var value = Payload();
        Assert.True(NotebookInkPayload.Valid(Pack(value), Patient, Page));
        value["strokes"] = Array.Empty<object>();
        Assert.True(NotebookPayloadService.ValidPayload(Pack(value), Patient, Page));
    }
    [Theory]
    [InlineData("formatVersion", 3)] [InlineData("pageWidthUnits", 0)] [InlineData("pageHeightUnits", 210)]
    public void UnsupportedVersionAndDimensionsAreRejected(string field, int value)
    {
        var data = Payload(); data[field] = value;
        Assert.False(NotebookInkPayload.Valid(Pack(data), Patient, Page));
    }
    [Fact]
    public void OwnershipTrailingBytesAndMalformedInputAreRejected()
    {
        var bytes = Pack(Payload());
        Assert.False(NotebookInkPayload.Valid(bytes, Guid.NewGuid(), Page));
        Assert.False(NotebookInkPayload.Valid(bytes, Patient, Guid.NewGuid()));
        Assert.False(NotebookInkPayload.Valid(bytes[..^1], Patient, Page));
        Assert.False(NotebookInkPayload.Valid([.. bytes, 0], Patient, Page));
        Assert.False(NotebookInkPayload.Valid([0xdf, 255, 255, 255, 255], Patient, Page));
    }
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)] [InlineData(211d)]
    public void UnsafeCoordinatesAreRejected(double value)
    {
        var data = Payload(); data["strokes"] = new object[] { Stroke(value) };
        Assert.False(NotebookInkPayload.Valid(Pack(data), Patient, Page));
    }
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-.1)] [InlineData(1.1)]
    public void UnsafePressureIsRejected(double value)
    {
        var data = Payload(); data["strokes"] = new object[] { Stroke(pressure: value) };
        Assert.False(NotebookInkPayload.Valid(Pack(data), Patient, Page));
    }
    [Fact]
    public void CountsIdsTimingAndByteLimitsAreBounded()
    {
        var data = Payload(); data["strokes"] = Enumerable.Range(0, NotebookInkPayload.MaxStrokes + 1).Select(i => Stroke(id: i)).ToArray();
        Assert.False(NotebookInkPayload.Valid(Pack(data), Patient, Page));
        var stroke = Stroke();
        stroke["points"] = Enumerable.Repeat(new object?[] { 0d, 0d, null, 0L }, NotebookInkPayload.MaxPointsPerStroke + 1).ToArray();
        data["strokes"] = new object[] { stroke };
        Assert.False(NotebookInkPayload.Valid(Pack(data), Patient, Page));
        stroke["points"] = new object[] { new object?[] { 0d, 0d, null, 1L } };
        Assert.False(NotebookInkPayload.Valid(Pack(data), Patient, Page));
        data["strokes"] = new object[] { Stroke(), Stroke() };
        Assert.False(NotebookInkPayload.Valid(Pack(data), Patient, Page));
        Assert.False(NotebookInkPayload.Valid(new byte[NotebookInkPayload.MaxBytes + 1], Patient, Page));
        data["strokes"] = Enumerable.Range(0, 6).Select(i => {
            var s = Stroke(id: i);
            s["points"] = Enumerable.Repeat(new object?[] { 0, 0, null, 0 }, 20000).ToArray(); return s;
        }).ToArray();
        Assert.False(NotebookInkPayload.Valid(Pack(data), Patient, Page));
    }
}
