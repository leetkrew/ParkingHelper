using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

public interface IArchiveFileShareService
{
    Task ShareAsync(ArchiveExportResult export);
}

public sealed class ArchiveFileShareService : IArchiveFileShareService
{
    public async Task ShareAsync(ArchiveExportResult export)
    {
        var path = Path.Combine(FileSystem.Current.CacheDirectory, export.FileName);
        await File.WriteAllBytesAsync(path, export.Content).ConfigureAwait(false);
        try
        {
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Export archived tickets",
                File = new ShareFile(path, export.ContentType)
            }).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
