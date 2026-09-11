using ParkingHelper.App.Services;
using ZXing;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class BarcodeRenderingTests
{
    [Theory]
    [InlineData("QrCode", "parking:ABC123", BarcodeFormat.QR_CODE)]
    [InlineData("Pdf417", "parking:ABC123", BarcodeFormat.PDF_417)]
    [InlineData("Aztec", "parking:ABC123", BarcodeFormat.AZTEC)]
    [InlineData("DataMatrix", "parking:ABC123", BarcodeFormat.DATA_MATRIX)]
    [InlineData("Code128", "1234567890", BarcodeFormat.CODE_128)]
    public void RenderedBarcodeDecodesWithOriginalValueAndSymbology(string format, string value, BarcodeFormat expected)
    {
        var barcode = new BarcodeRenderingService().Render(value, format);
        Assert.Null(barcode.Message);
        Assert.NotNull(barcode.Modules);
        // Rasterize exactly the module grid used by the view, with a quiet border.
        const int scale = 5;
        var width = barcode.Width * scale + 40;
        var height = (barcode.Height == 1 ? 40 : barcode.Height) * scale + 40;
        var rgb = Enumerable.Repeat((byte)255, width * height * 3).ToArray();
        for (var y = 20; y < height - 20; y++)
            for (var x = 20; x < width - 20; x++)
                if (barcode.Modules[(x - 20) / scale, barcode.Height == 1 ? 0 : (y - 20) / scale])
                { var index = (y * width + x) * 3; rgb[index] = rgb[index + 1] = rgb[index + 2] = 0; }
        var reader = new BarcodeReaderGeneric { Options = new ZXing.Common.DecodingOptions { TryHarder = true, PossibleFormats = [expected] } };
        var decoded = reader.Decode(rgb, width, height, RGBLuminanceSource.BitmapFormat.RGB24);
        Assert.NotNull(decoded);
        Assert.Equal(value, decoded.Text);
        Assert.Equal(expected, decoded.BarcodeFormat);
    }

    [Fact]
    public void EveryDecoderOnlyFormatReturnsFallbackUsingInstalledWriterCapabilities()
    {
        var renderer = new BarcodeRenderingService();
        foreach (var format in Enum.GetValues<ZXing.Net.Maui.BarcodeFormat>())
        {
            if (MultiFormatWriter.SupportedWriters.Contains((BarcodeFormat)(int)format)) continue;
            var result = renderer.Render("1234567890", format.ToString());
            Assert.Null(result.Modules);
            Assert.Contains("cannot be regenerated", result.Message);
        }
    }

    [Theory]
    [InlineData("MaxiCode", "ticket")]
    [InlineData("Rss14", "1234567890123")]
    [InlineData("NotAFormat", "ticket")]
    [InlineData("QrCode", "")]
    [InlineData("Ean13", "not digits")]
    public void UnsupportedFormatOrInvalidContentReturnsMessageWithoutSubstitution(string format, string value)
    {
        var result = new BarcodeRenderingService().Render(value, format);
        Assert.Null(result.Modules);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }
}
