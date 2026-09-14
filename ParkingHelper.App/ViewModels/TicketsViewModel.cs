using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.ViewModels;

public sealed class TicketRowViewModel(ParkingTicket ticket) : INotifyPropertyChanged
{
    public Guid Id => ticket.Id;
    public string PlateNumber => ticket.PlateNumberSnapshot ?? "Saved plate";
    public string SavedLocalTime => "Saved " + ticket.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
    public string ArchivedLocalTime => ticket.ArchivedUtc is { } date ? "Archived " + date.ToLocalTime().ToString("MMM d, yyyy · h:mm tt") : "";
    public bool IsArchived => ticket.State == ParkingTicketState.Archived;
    public string Duration { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void UpdateDuration(DateTime utcNow)
    {
        var next = "Duration: " + TicketDuration.Format(ticket, utcNow);
        if (next == Duration) return;
        Duration = next;
        PropertyChanged?.Invoke(this, new(nameof(Duration)));
    }
}

public sealed class TicketsViewModel(ITicketService tickets, TimeProvider clock, ILogger<TicketsViewModel> logger, GoogleDriveConnection? drive = null) : INotifyPropertyChanged
{
    private int loadVersion;
    public ObservableCollection<TicketRowViewModel> Items { get; } = [];
    public bool IsArchived { get; private set; }
    public bool IsActive => !IsArchived;
    public bool IsBusy { get; private set; }
    public bool IsRefreshing { get; private set; }
    public string Error { get; private set; } = "";
    public bool HasError => Error.Length > 0;
    public string EmptyTitle => IsArchived ? "No archived tickets yet." : "No active tickets";
    public string EmptyDetail => IsArchived ? "" : "Scan a parking ticket to get started.";
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync(bool? archived = null)
    {
        var version = ++loadVersion;
        if (archived.HasValue) IsArchived = archived.Value;
        IsBusy = true;
        Error = "";
        Items.Clear();
        Notify();
        try
        {
            var rows = IsArchived ? await tickets.GetArchivedTicketsAsync() : await tickets.GetActiveTicketsAsync();
            if (version != loadVersion) return;
            foreach (var ticket in rows)
            {
                var row = new TicketRowViewModel(ticket);
                row.UpdateDuration(clock.GetUtcNow().UtcDateTime);
                Items.Add(row);
            }
            drive?.Diagnostics?.ListLoaded(IsArchived, Items.Count, clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception exception)
        {
            if (version != loadVersion) return;
            drive?.Diagnostics?.Update(d => d with { ListError = exception.GetType().Name });
            logger.LogWarning(exception, "Could not load tickets");
            Error = "Couldn’t load tickets. Please retry.";
        }
        finally { if (version == loadVersion) { IsBusy = false; Notify(); } }
    }

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        Notify();
        try
        {
            if (drive?.IsConnected == true) await drive.SyncAsync();
        }
        finally
        {
            // Cloud failures are reported by the shared connection; SQLite remains usable.
            try { await LoadAsync(); }
            finally { IsRefreshing = false; Notify(); }
        }
    }

    public void UpdateDurations()
    {
        if (IsArchived) return;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var item in Items) item.UpdateDuration(now);
    }

    private void Notify() => PropertyChanged?.Invoke(this, new(null));
}
