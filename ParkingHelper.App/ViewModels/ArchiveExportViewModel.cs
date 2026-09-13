using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.ViewModels;

public sealed class ArchiveExportTicketViewModel(ParkingTicket ticket) : INotifyPropertyChanged
{
    private bool isSelected;
    public Guid Id => ticket.Id;
    public string PlateNumber => ticket.PlateNumberSnapshot ?? "Saved plate";
    public string Summary => $"{PlateNumber} · {ticket.ArchivedUtc?.ToLocalTime():MMM d, yyyy h:mm tt}";
    public bool IsSelected { get => isSelected; set { if (isSelected == value) return; isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ArchiveExportViewModel(
    IArchiveExportService exporter,
    IArchiveFileShareService fileShare,
    ITicketService tickets, ILogger<ArchiveExportViewModel> logger) : INotifyPropertyChanged
{
    public ObservableCollection<ArchiveExportTicketViewModel> Items { get; } = [];
    public IReadOnlyList<ArchiveExportScope> Scopes { get; } = Enum.GetValues<ArchiveExportScope>();
    public IReadOnlyList<ArchiveExportFormat> Formats { get; } = Enum.GetValues<ArchiveExportFormat>();
    private ArchiveExportScope scope = ArchiveExportScope.All;
    private ArchiveExportFormat format = ArchiveExportFormat.Csv;
    public ArchiveExportScope Scope { get => scope; set { scope = value; Notify(); } }
    public ArchiveExportFormat Format { get => format; set { format = value; Notify(); } }
    public bool IsSelectedScope => Scope == ArchiveExportScope.Selected;
    public bool IsDateRangeScope => Scope == ArchiveExportScope.DateRange;
    public DateTime? FromDate { get; set; } = DateTime.Today;
    public DateTime? ToDate { get; set; } = DateTime.Today;
    public bool HasLoadError { get; private set; }
    public bool IsBusy { get; private set; }
    public bool CanExport => !IsBusy;
    public string Status { get; private set; } = "";
    public bool HasStatus => Status.Length > 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        HasLoadError = false;
        Status = "";
        Notify();
        try
        {
            var archived = await tickets.GetArchivedTicketsAsync();
            Items.Clear();
            foreach (var ticket in archived.Where(ticket => ticket.State == ParkingTicketState.Archived
                         && ticket.ArchivedUtc != null && ticket.DeletedUtc == null))
                Items.Add(new ArchiveExportTicketViewModel(ticket));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load archived tickets for export");
            HasLoadError = true;
            Status = "Couldn’t load archived tickets. Tap Reload archives to try again.";
        }
        finally { IsBusy = false; Notify(); }
    }

    public void SelectAll() { foreach (var item in Items) item.IsSelected = true; }
    public void ClearSelection() { foreach (var item in Items) item.IsSelected = false; }

    public async Task<bool> ExportAsync()
    {
        if (IsBusy) return false;
        IsBusy = true; Status = ""; Notify();
        try
        {
            var request = new ArchiveExportRequest(Scope, Format,
                Items.Where(item => item.IsSelected).Select(item => item.Id).ToArray(),
                FromDate is { } from ? DateOnly.FromDateTime(from) : null,
                ToDate is { } to ? DateOnly.FromDateTime(to) : null);
            var result = await exporter.ExportAsync(request);
            await fileShare.ShareAsync(result);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (ArchiveExportException exception) { Status = exception.Message; return false; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not create or share archived ticket export");
            Status = "Couldn’t create or share the export. Please try again.";
            return false;
        }
        finally { IsBusy = false; Notify(); }
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(null));
        PropertyChanged?.Invoke(this, new(nameof(IsSelectedScope)));
        PropertyChanged?.Invoke(this, new(nameof(IsDateRangeScope)));
        PropertyChanged?.Invoke(this, new(nameof(CanExport)));
    }
}
