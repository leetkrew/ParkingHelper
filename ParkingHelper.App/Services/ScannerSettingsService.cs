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

public static class ScannerCameraSelection
{
    // Falling back is a session choice; keep the preference so reconnecting can use it again.
    public static string? Resolve(string? preferredId, IEnumerable<string> availableIds) =>
        preferredId != null && availableIds.Contains(preferredId, StringComparer.Ordinal) ? preferredId : null;
}

public static class ScannerFormatCatalog
{
    public static IReadOnlyList<BarcodeFormat> Values { get; } = Enum.GetValues<BarcodeFormat>().Distinct().ToArray();
    // Keep specialist formats in Values for explicit selection, never in the Auto profile.
    public static BarcodeFormat Auto { get; } = BarcodeFormat.QrCode | BarcodeFormat.Pdf417
        | BarcodeFormat.Aztec | BarcodeFormat.DataMatrix | BarcodeFormat.Code128 | BarcodeFormat.Code39
        | BarcodeFormat.Ean13 | BarcodeFormat.Ean8 | BarcodeFormat.UpcA | BarcodeFormat.UpcE | BarcodeFormat.Itf;
    public static BarcodeFormat Resolve(string? id) => Values.FirstOrDefault(f => f.ToString() == id) is var format
        && format != 0 ? format : Auto;
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
