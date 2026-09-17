using System.Text;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

// Support diagnostics: only explicitly allowed identity, metadata, counts and status.
public sealed class GoogleDriveDiagnosticsReport
{
    private readonly GoogleDriveConnection connection;
    private readonly GoogleDriveSyncDiagnostics diagnostics;
    private readonly IGoogleDriveOAuthConfiguration? configuration;
    private readonly string signingIdentity;

    public GoogleDriveDiagnosticsReport(GoogleDriveConnection connection, GoogleDriveSyncDiagnostics diagnostics,
        IServiceProvider services)
    {
        this.connection = connection;
        this.diagnostics = diagnostics;
        configuration = services.GetService<IGoogleDriveOAuthConfiguration>();
        signingIdentity = ReadSigningIdentity();
    }

    public string Build()
    {
        var d = diagnostics.Snapshot;
        var report = new StringBuilder();
        report.AppendLine("Parking Helper — Drive Diagnostics");
        report.AppendLine($"Report UTC: {DateTime.UtcNow:O}");
        report.AppendLine($"Google account email: {connection.Account?.Email ?? "Unavailable"}");
        report.AppendLine($"Application/package ID: {AppInfo.Current.PackageName}");
        report.AppendLine($"Build: {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})");
        report.AppendLine($"Configured OAuth client ID: {configuration?.ClientId ?? "Unavailable"}");
        report.AppendLine($"Android signing certificate SHA-1: {signingIdentity}");
        report.AppendLine("Android GIS resolves its credential from package + signing certificate; configured client ID alone is not proof of the effective project.");
        report.AppendLine($"Connected: {connection.IsConnected}; pending sync: {connection.NeedsSynchronization}");
        var failure = connection.LastConnectionFailure;
        report.AppendLine($"Last connection failure UTC: {Time(failure?.Utc)}; error types: {failure?.ErrorTypes ?? "None"}; Google status: {failure?.GoogleStatusCode?.ToString() ?? "n/a"}");
        report.AppendLine("Cloud document: parkinghelper-sync-v1.json; space: appDataFolder; match: parkingHelperSync=1");
        report.AppendLine($"Run: {d.RunId}; attempt: {d.Attempt}; started UTC: {Time(d.StartedUtc)}");
        report.AppendLine($"Outcome: {d.Outcome}; stage: {d.Stage}; error type: {d.ErrorType ?? "None"}; HTTP: {d.HttpStatus?.ToString() ?? "n/a"}");
        report.AppendLine($"Matching sync files found during download: {d.MatchingFiles?.ToString() ?? "Not observed"}");
        File(report, "Download canonical", d.DownloadedFile);
        Counts(report, "Local before sync", d.LocalBefore);
        Counts(report, "Cloud after download", d.CloudDownloaded);
        Counts(report, "Merge output", d.MergeOutput);
        Counts(report, "SQLite after apply (read back)", d.SQLiteAfter);
        report.AppendLine($"SQLite diagnostic read-back error: {d.SQLiteReadBackError ?? "None"}");
        if (d.Persistence is { } persistence)
        {
            report.AppendLine($"SQLite apply transaction: {persistence.Transaction}; reason: {persistence.FailureReason ?? "None"}");
            report.AppendLine($"SQLite verification: expected={persistence.ExpectedIds.Count}; read back={persistence.ReadBackIds.Count}; missing={persistence.MissingIds.Count}; mismatched={persistence.MismatchedIds.Count}; unexpected={persistence.UnexpectedIds.Count}");
        }
        report.AppendLine($"Upload: {d.UploadOutcome}");
        File(report, "Uploaded", d.UploadedFile);
        Counts(report, "Upload accepted record counts", d.UploadedCounts);
        report.AppendLine($"Last successful full sync UTC (this process): {Time(d.LastSuccessfulUtc)}");
        Counts(report, "Current SQLite", d.CurrentLocal);
        report.AppendLine($"Current SQLite observed UTC: {Time(d.CurrentLocalUtc)}");
        report.AppendLine($"Explicit read-only verification: {d.VerificationOutcome ?? "Not run"}; UTC: {Time(d.VerifiedUtc)}; matching files: {d.VerifiedMatchingFiles?.ToString() ?? "Not observed"}");
        File(report, "Verified canonical", d.VerifiedFile);
        Counts(report, "Verified cloud document", d.VerifiedCloud);
        report.AppendLine($"Active UI list: {d.ActiveListCount?.ToString() ?? "Not loaded"}; loaded UTC: {Time(d.ActiveListLoadedUtc)}");
        report.AppendLine($"Archived UI list: {d.ArchivedListCount?.ToString() ?? "Not loaded"}; loaded UTC: {Time(d.ArchivedListLoadedUtc)}");
        report.AppendLine($"UI list error type: {d.ListError ?? "None"}");
        return report.ToString();
    }

    private static string Time(DateTime? utc) => utc?.ToString("O") ?? "Not observed";
    private static void File(StringBuilder report, string title, DriveDiagnosticFile? file) => report.AppendLine(
        $"{title} file ID: {file?.Id ?? "Not observed"}; version: {file?.Version ?? "Not observed"}; modified UTC: {file?.ModifiedUtc ?? "Not observed"}");
    private static void Counts(StringBuilder report, string title, DriveDiagnosticCounts? counts) => report.AppendLine(counts is null
        ? $"{title}: Not observed"
        : $"{title}: plates={counts.Plates}, plate tombstones={counts.PlateTombstones}, tickets={counts.Tickets} (active={counts.Active}, archived={counts.Archived}, deletion tombstones={counts.Deleted})");

    private static string ReadSigningIdentity()
    {
#if ANDROID
        try
        {
#pragma warning disable CS0618
            var info = Android.App.Application.Context.PackageManager?.GetPackageInfo(
                AppInfo.Current.PackageName, Android.Content.PM.PackageInfoFlags.Signatures);
            return string.Join(", ", info?.Signatures?.Select(signature =>
                Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(signature.ToByteArray()!))) ?? []);
#pragma warning restore CS0618
        }
        catch { return "Unavailable"; }
#else
        return "Not Android";
#endif
    }
}
