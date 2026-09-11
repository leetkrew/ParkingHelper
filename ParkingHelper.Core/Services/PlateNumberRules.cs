namespace ParkingHelper.Core.Services;

public static class PlateNumberRules
{
    public const int MaxLength = 64;

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new PlateOperationException("Enter a plate number.");
        if (normalized.Length > MaxLength)
            throw new PlateOperationException($"Use {MaxLength} characters or fewer for a plate number.");
        return normalized;
    }
}
