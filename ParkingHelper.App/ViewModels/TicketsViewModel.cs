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
    public string StateAction => IsArchived ? "Restore" : "Archive";
    public string StateActionIcon => IsArchived ? "action_restore.png" : "action_archive.png";
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
    private int refreshVersion;
    private CancellationTokenSource? loadSource;
    private CancellationTokenSource? refreshSource;
    private bool active = true;
    private bool observingDrive;
    private bool loaded;
    private DateTime? observedSync;
    private Action<Action> dispatch = action => action();
    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public ObservableCollection<TicketRowViewModel> Items { get; } = [];
    public bool IsArchived { get; private set; }
    public bool IsActive => !IsArchived;
    public bool IsLoading { get; private set; }
    public bool IsBusy => IsLoading;
    public bool IsRefreshing { get; private set; }
    public bool IsSyncing => drive?.IsBusy == true;
    public string Error { get; private set; } = "";
    public bool HasError => Error.Length > 0;
    public bool ShowEmpty => active && loaded && !IsLoading && !HasError && Items.Count == 0;
    public bool ShowContent => !IsLoading;
    public bool ShowTickets => !IsLoading && Items.Count > 0;
    public string EmptyTitle => IsArchived ? "No archived tickets yet." : "No active tickets";
    public string EmptyDetail => IsArchived ? "" : "Scan a parking ticket to get started.";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Activate(Action<Action>? dispatcher = null)
    {
        active = true;
        if (dispatcher is not null) dispatch = dispatcher;
        if (drive is null || observingDrive) return;
        observedSync = drive.LastSuccessfulSync;
        drive.Changed += OnDriveChanged;
        observingDrive = true;
    }

    public void Deactivate()
    {
        active = false;
        if (drive is not null && observingDrive) drive.Changed -= OnDriveChanged;
        observingDrive = false;
        ++loadVersion;
        ++refreshVersion;
        loadSource?.Cancel();
        refreshSource?.Cancel();
        loadSource = refreshSource = null;
        IsLoading = IsRefreshing = false;
        Notify();
    }

    private void OnDriveChanged() => dispatch(async () =>
    {
        if (!active) return;
        Notify(); // Sync has its own state; it never owns IsLoading.
        if (drive?.LastSuccessfulSync is not { } success || success == observedSync) return;
        observedSync = success;
        if (!IsRefreshing) await ReloadAsync(); // Pull-to-refresh already owns its final local reload.
    });

    public event Action? Reloading;

    public Task LoadAsync(bool? archived = null) => LoadCoreAsync(archived, quiet: false);
    public Task ReloadAsync() => LoadCoreAsync(null, quiet: true);

    private async Task LoadCoreAsync(bool? archived, bool quiet)
    {
        if (!active) return;
        var version = ++loadVersion;
        loadSource?.Cancel();
        using var source = new CancellationTokenSource();
        loadSource = source;
        if (archived.HasValue && archived != IsArchived)
        {
            IsArchived = archived.Value;
            Items.Clear();
            loaded = false;
        }
        var requestedArchived = IsArchived;
        IsLoading = !IsRefreshing && (!quiet || !loaded);
        Error = "";
        try
        {
            Reloading?.Invoke();
            Notify();
            // SQLite reads may not accept cancellation. Cancel the UI wait, not the shared repository.
            var read = requestedArchived ? tickets.GetArchivedTicketsAsync() : tickets.GetActiveTicketsAsync();
            var rows = await read.WaitAsync(source.Token);
            if (version != loadVersion || !active) return;
            Items.Clear();
            foreach (var ticket in rows)
            {
                var row = new TicketRowViewModel(ticket);
                row.UpdateDuration(clock.GetUtcNow().UtcDateTime);
                Items.Add(row);
            }
            loaded = true;
            drive?.Diagnostics?.ListLoaded(requestedArchived, Items.Count, clock.GetUtcNow().UtcDateTime);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (version != loadVersion || !active) return;
            drive?.Diagnostics?.Update(d => d with { ListError = exception.GetType().Name });
            logger.LogWarning(exception, "Could not load tickets");
            Error = "Couldn’t load your saved tickets. Please retry.";
        }
        finally
        {
            if (version == loadVersion)
            {
                loadSource = null;
                IsLoading = false;
                Notify();
            }
        }
    }

    public async Task RefreshAsync()
    {
        if (!active || IsRefreshing) return;
        var version = ++refreshVersion;
        using var source = new CancellationTokenSource();
        refreshSource = source;
        IsRefreshing = true;
        try
        {
            Notify();
            if (drive?.IsConnected == true)
                // A hung Drive operation cannot hold the gesture indefinitely. The shared
                // sync continues under the coordinator; navigation only cancels this UI wait.
                await drive.SyncAsync().WaitAsync(RefreshTimeout, source.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            logger.LogWarning("Ticket refresh cloud wait ended: {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            try
            {
                if (version == refreshVersion && active && !source.IsCancellationRequested)
                    await ReloadAsync();
            }
            finally
            {
                if (version == refreshVersion)
                {
                    refreshSource = null;
                    IsRefreshing = false;
                    Notify();
                }
            }
        }
    }

    public Task ChangeStateAsync(Guid id, ParkingTicketState state) => state switch
    {
        ParkingTicketState.Archived => tickets.ArchiveTicketAsync(id),
        ParkingTicketState.Active => tickets.RestoreTicketAsync(id),
        ParkingTicketState.Deleted => tickets.DeleteTicketAsync(id),
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public void UpdateDurations()
    {
        if (IsArchived) return;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var item in Items) item.UpdateDuration(now);
    }

    private void Notify()
    {
        foreach (var property in new[] { nameof(IsLoading), nameof(IsBusy), nameof(IsRefreshing), nameof(IsSyncing),
            nameof(IsArchived), nameof(IsActive), nameof(Error), nameof(HasError), nameof(ShowEmpty), nameof(ShowTickets),
            nameof(EmptyTitle), nameof(EmptyDetail), nameof(ShowContent) })
            PropertyChanged?.Invoke(this, new(property));
    }
}
