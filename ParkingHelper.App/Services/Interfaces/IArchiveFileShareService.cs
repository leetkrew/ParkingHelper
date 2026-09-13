using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

public interface IArchiveFileShareService
{
    Task ShareAsync(ArchiveExportResult export);
}
