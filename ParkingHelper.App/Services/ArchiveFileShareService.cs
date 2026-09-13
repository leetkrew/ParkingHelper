using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

public sealed class ArchiveFileShareService : IArchiveFileShareService
{
    public async Task ShareAsync(ArchiveExportResult export)
    {
        ArgumentNullException.ThrowIfNull(export);
        // Use a unique cache directory so a later export cannot replace a file still being shared.
        var directory = Path.Combine(FileSystem.Current.CacheDirectory, "archive-exports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, export.FileName);
        await File.WriteAllBytesAsync(path, export.Content).ConfigureAwait(false);
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Export archived tickets",
            File = new ShareFile(path, export.ContentType)
        })).ConfigureAwait(false);
        // Android can return when the chooser opens, before the recipient has read the file.
        // Leave the file in OS-managed cache. Native cancellation is handled by the caller.
    }
}
