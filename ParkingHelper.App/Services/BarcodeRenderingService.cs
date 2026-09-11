using ZXing;

namespace ParkingHelper.App.Services;

// The UI receives a module grid, not ZXing controls or barcode types.
public sealed record BarcodeRenderResult(bool[,]? Modules, string? Message)
{
    public int Width => Modules?.GetLength(0) ?? 0;
    public int Height => Modules?.GetLength(1) ?? 0;
}

public interface IBarcodeRenderingService
{
    BarcodeRenderResult Render(string value, string originalFormat);
}

public sealed class BarcodeRenderingService : IBarcodeRenderingService
{
    public BarcodeRenderResult Render(string value, string originalFormat)
    {
        // Match the exact persisted MAUI name. Never substitute a different symbology.
        var known = Enum.GetValues<ZXing.Net.Maui.BarcodeFormat>()
            .Where(format => format.ToString() == originalFormat).ToArray();
        if (known.Length != 1 || string.IsNullOrWhiteSpace(value))
            return new(null, "This ticket’s barcode cannot be displayed. Your saved ticket details are still available.");
        var format = (BarcodeFormat)(int)known[0];
        if (!MultiFormatWriter.SupportedWriters.Contains(format))
            return new(null, "This barcode format cannot be regenerated. Your original ticket data is preserved.");
        try
        {
            var matrix = new MultiFormatWriter().encode(value, format, 0, 0,
                new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8", [EncodeHintType.MARGIN] = format == BarcodeFormat.QR_CODE ? 4 : 10 });
            var modules = new bool[matrix.Width, matrix.Height];
            for (var y = 0; y < matrix.Height; y++)
                for (var x = 0; x < matrix.Width; x++) modules[x, y] = matrix[x, y];
            return new(modules, null);
        }
        catch (Exception)
        {
            return new(null, "This barcode format or content cannot be regenerated. Your original ticket data is preserved.");
        }
    }
}
