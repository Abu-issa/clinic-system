using MessagePack;

namespace Clinic.Application.Notebook;

/// <summary>Version 2 MessagePack: fixed-depth maps, bounded arrays, no extensions/compression.</summary>
public static class NotebookInkPayload
{
    public const int Version = 2, MaxBytes = 4 * 1024 * 1024, MaxStrokes = 4096,
        MaxPointsPerStroke = 20000, MaxPoints = 100000;
    public const long MaxTimeMicros = 86_400_000_000, MaxStrokeId = 9_007_199_254_740_991;

    public static bool Valid(byte[] bytes, Guid patientId, Guid pageId)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) return false;
        try
        {
            var r = new MessagePackReader(bytes);
            if (r.ReadMapHeader() != 6) return false;
            var fields = new HashSet<string>();
            for (var i = 0; i < 6; i++)
            {
                var field = r.ReadString();
                if (field is null || !fields.Add(field)) return false;
                switch (field)
                {
                    case "formatVersion": if (r.ReadInt32() != Version) return false; break;
                    case "patientId": if (!Guid.TryParse(r.ReadString(), out var patient) || patient != patientId) return false; break;
                    case "pageId": if (!Guid.TryParse(r.ReadString(), out var page) || page != pageId) return false; break;
                    case "pageWidthUnits": if (r.ReadDouble() != 210) return false; break;
                    case "pageHeightUnits": if (r.ReadDouble() != 297) return false; break;
                    case "strokes": if (!Strokes(ref r)) return false; break;
                    default: return false;
                }
            }
            return r.End;
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException or
            OverflowException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static bool Strokes(ref MessagePackReader r)
    {
        var count = r.ReadArrayHeader();
        if (count > MaxStrokes) return false;
        var ids = new HashSet<long>();
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            if (r.ReadMapHeader() != 4) return false;
            var fields = new HashSet<string>();
            for (var j = 0; j < 4; j++)
            {
                var field = r.ReadString();
                if (field is null || !fields.Add(field)) return false;
                switch (field)
                {
                    case "id":
                        var id = r.ReadInt64();
                        if (id < 0 || id > MaxStrokeId || !ids.Add(id)) return false;
                        break;
                    case "color": if (r.ReadUInt64() > uint.MaxValue) return false; break;
                    case "width": if (!Number(ref r, 0.01, 10)) return false; break;
                    case "points":
                        var points = r.ReadArrayHeader();
                        if (points < 1 || points > MaxPointsPerStroke || (total += points) > MaxPoints) return false;
                        long previous = 0;
                        for (var k = 0; k < points; k++)
                        {
                            if (r.ReadArrayHeader() != 4 || !Number(ref r, 0, 210) || !Number(ref r, 0, 297)) return false;
                            if (!r.TryReadNil() && !Number(ref r, 0, 1)) return false;
                            var time = r.ReadInt64();
                            if (time < previous || time > MaxTimeMicros || (k == 0 && time != 0)) return false;
                            previous = time;
                        }
                        break;
                    default: return false;
                }
            }
        }
        return true;
    }
    private static bool Number(ref MessagePackReader r, double min, double max)
    {
        var value = r.ReadDouble();
        return double.IsFinite(value) && value >= min && value <= max;
    }
}
