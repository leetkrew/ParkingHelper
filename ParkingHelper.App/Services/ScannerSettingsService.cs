using System.Text.RegularExpressions;
using ZXing.Net.Maui;
using Microsoft.Maui.Storage;

namespace ParkingHelper.App.Services;

public sealed record ScannerFormatOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

public interface IScannerSettingsService
{
    IReadOnlyList<ScannerFormatOption> Formats { get; }
    string? Format { get; set; }
    string? PreferredCameraId { get; set; }
}

public static class ScannerFormatCatalog
{
    public static IReadOnlyList<BarcodeFormat> Values { get; } = Enum.GetValues<BarcodeFormat>().Distinct().ToArray();
    // BarcodeFormats.All deliberately excludes Pharmacode in 0.10.4.
    public static BarcodeFormat All { get; } = Values.Aggregate((BarcodeFormat)0, (mask, format) => mask | format);
    public static BarcodeFormat Resolve(string? id) => Values.FirstOrDefault(f => f.ToString() == id) is var format
        && format != 0 ? format : All;
    public static string DisplayName(string name) => name switch
    {
        "QrCode" => "QR Code", "Pdf417" => "PDF417", "Imb" => "Intelligent Mail",
        "UpcEanExtension" => "UPC/EAN extension (supplement)",
        _ => Regex.Replace(Regex.Replace(name, "([a-z])([A-Z])", "$1 $2"), "([A-Za-z])([0-9])", "$1 $2")
    };
}

public sealed class ScannerSettingsService(IPreferences preferences) : IScannerSettingsService
{
    public IReadOnlyList<ScannerFormatOption> Formats { get; } =
        new[] { new ScannerFormatOption(null, "Auto") }.Concat(ScannerFormatCatalog.Values.Select(
            f => new ScannerFormatOption(f.ToString(), ScannerFormatCatalog.DisplayName(f.ToString())))).ToArray();
    public string? Format
    {
        get
        {
            var value = preferences.Get("scanner.format", "");
            return Formats.Any(f => f.Id == value) ? value : null;
        }
        set => preferences.Set("scanner.format", Formats.Any(f => f.Id == value) ? value ?? "" : "");
    }
    public string? PreferredCameraId
    {
        get => preferences.Get("scanner.camera", "") is { Length: > 0 } id ? id : null;
        set => preferences.Set("scanner.camera", value ?? "");
    }
}
