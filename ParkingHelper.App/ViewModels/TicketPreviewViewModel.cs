using System.ComponentModel;
using System.Collections.ObjectModel;
using ParkingHelper.App.Services;
using Microsoft.Extensions.Logging;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.ViewModels;

public sealed class TicketPlateChoice(VehiclePlate plate) : INotifyPropertyChanged
{
    public VehiclePlate Plate { get; } = plate;
    public string PlateNumber => Plate.PlateNumber;
    public Guid Id => Plate.Id;
    public bool IsSelected { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void SetSelected(bool selected)
    {
        if (IsSelected == selected) return;
        IsSelected = selected;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
    }
}

public sealed class TicketPreviewViewModel(ITicketService tickets, TimeProvider clock,
    ILogger<TicketPreviewViewModel> logger, IBarcodeRenderingService? renderer = null, IPlateService? plates = null) : INotifyPropertyChanged
{
    private ParkingTicket? ticket;
    private int loadVersion;
    public BarcodeRenderResult? Barcode { get; private set; }
    public bool IsActive => ticket?.State == ParkingTicketState.Active;
    public bool IsArchived => ticket?.State == ParkingTicketState.Archived;
    public bool CanAct => IsLoaded && !IsBusy && !IsEditingPlate;
    public ObservableCollection<TicketPlateChoice> PlateChoices { get; } = [];
    public bool IsEditingPlate { get; private set; }
    public bool CanConfirmPlate => IsEditingPlate && !IsBusy && SelectedPlate != null;
    private VehiclePlate? selectedPlate;
    public VehiclePlate? SelectedPlate
    {
        get => selectedPlate;
        private set { selectedPlate = value; Notify(); }
    }
    public void SelectPlate(Guid id)
    {
        if (!IsEditingPlate || IsBusy) return;
        var choice = PlateChoices.FirstOrDefault(item => item.Id == id);
        if (choice == null) return;
        SelectedPlate = choice.Plate;
        foreach (var item in PlateChoices) item.SetSelected(item.Id == id);
    }
    public string PlateNumber => ticket?.PlateNumberSnapshot ?? "Saved plate";
    public DateTime EntryDate { get; set; }
    public TimeSpan EntryTime { get; set; }
    public string Details => ticket == null ? "" :
        $"Plate number: {PlateNumber}\n\nBarcode format: {ticket.BarcodeFormat}\n\nBarcode value: {ticket.BarcodeValue}\n\nScanned: {ticket.ScannedUtc.ToLocalTime():MMM d, yyyy · h:mm:ss tt}\n\nEntry: {ticket.EntryUtc.ToLocalTime():MMM d, yyyy · h:mm:ss tt}" +
        (ticket.ArchivedUtc is { } archived ? $"\n\nArchived: {archived.ToLocalTime():MMM d, yyyy · h:mm:ss tt}" : "") +
        $"\n\nStatus: {ticket.State}\n\nTicket ID: {ticket.Id}";
    public IReadOnlyList<KeyValuePair<string, string>> DetailFields
    {
        get
        {
            if (ticket == null) return [];
            List<KeyValuePair<string, string>> fields =
            [
                new("Plate number", PlateNumber),
                new("Barcode format", ticket.BarcodeFormat),
                new("Barcode value", ticket.BarcodeValue),
                new("Scanned", ticket.ScannedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm:ss tt")),
                new("Entry", ticket.EntryUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm:ss tt"))
            ];
            if (ticket.ArchivedUtc is { } archived)
                fields.Add(new("Archived", archived.ToLocalTime().ToString("MMM d, yyyy · h:mm:ss tt")));
            fields.Add(new("Status", ticket.State.ToString()));
            fields.Add(new("Ticket ID", ticket.Id.ToString()));
            return fields;
        }
    }
    public string SavedLocalTime => ticket?.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm:ss tt") ?? "";
    public string Duration { get; private set; } = "00:00:00";
    public string Status { get; private set; } = "Loading ticket…";
    public bool IsLoaded => ticket != null;
    public bool IsBusy { get; private set; }
    public bool CanRetry => !IsBusy && !IsLoaded;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync(Guid id)
    {
        var version = ++loadVersion;
        ticket = null;
        IsEditingPlate = false;
        PlateChoices.Clear();
        selectedPlate = null;
        Barcode = null;
        IsBusy = true;
        Status = "Loading ticket…";
        Notify();
        try
        {
            var saved = await tickets.GetTicketAsync(id);
            if (version != loadVersion) return;
            ticket = saved?.State == ParkingTicketState.Deleted ? null : saved;
            Status = ticket == null ? "This ticket could not be found." : ticket.State == ParkingTicketState.Active ? "Active ticket" : "Archived ticket";
            Barcode = ticket == null ? null : (renderer ?? new BarcodeRenderingService()).Render(ticket.BarcodeValue, ticket.BarcodeFormat);
            UpdateDuration();
        }
        catch (Exception exception)
        {
            if (version != loadVersion) return;
            logger.LogWarning(exception, "Could not load ticket preview {TicketId}", id);
            Status = "Couldn’t load this ticket. Please retry.";
        }
        finally
        {
            if (version == loadVersion) { IsBusy = false; Notify(); }
        }
    }

    public void UpdateDuration()
    {
        if (ticket == null) return;
        Duration = TicketDuration.Format(ticket, clock.GetUtcNow().UtcDateTime);
        PropertyChanged?.Invoke(this, new(nameof(Duration)));
    }

    public async Task BeginEditPlateAsync()
    {
        if (!CanAct) return;
        var version = loadVersion;
        IsBusy = true;
        Notify();
        try
        {
            var choices = await (plates ?? throw new InvalidOperationException("Plate service unavailable")).GetPlatesAsync();
            if (version != loadVersion) return;
            PlateChoices.Clear();
            foreach (var plate in choices.OrderBy(plate => plate.SortOrder))
            {
                var choice = new TicketPlateChoice(plate);
                choice.SetSelected(plate.Id == ticket!.VehiclePlateId);
                PlateChoices.Add(choice);
            }
            selectedPlate = choices.FirstOrDefault(plate => plate.Id == ticket!.VehiclePlateId);
            EntryDate = ticket!.EntryUtc.ToLocalTime().Date;
            EntryTime = ticket.EntryUtc.ToLocalTime().TimeOfDay;
            IsEditingPlate = true;
            Status = choices.Count == 0 ? "No saved plates are available." : "Choose a saved plate and entry date/time, then save.";
        }
        catch (Exception exception)
        {
            if (version != loadVersion) return;
            logger.LogWarning(exception, "Could not load ticket plate choices");
            Status = "Couldn’t load saved plates. Please try again.";
        }
        finally { if (version == loadVersion) { IsBusy = false; Notify(); } }
    }

    public void CancelEditPlate()
    {
        if (IsBusy) return;
        IsEditingPlate = false;
        selectedPlate = null;
        Status = IsArchived ? "Archived ticket" : "Active ticket";
        Notify();
    }

    public async Task<bool> ConfirmPlateAsync()
    {
        if (!CanConfirmPlate || ticket == null) return false;
        var plateId = SelectedPlate!.Id;

        IsBusy = true;
        Notify();
        try
        {
            var local = DateTime.SpecifyKind(EntryDate.Date + EntryTime, DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local))
                throw new TicketOperationException("This local time does not exist because the clocks change. Choose another time.");
            // Preserve exact ticks and the original offset when the controls were not changed.
            var entryUtc = local == DateTime.SpecifyKind(ticket.EntryUtc.ToLocalTime(), DateTimeKind.Unspecified)
                ? ticket.EntryUtc : TimeZoneInfo.ConvertTimeToUtc(local);
            ticket = await tickets.EditTicketAsync(ticket.Id, plateId, entryUtc);
            IsEditingPlate = false;
            Status = "Ticket updated";
            UpdateDuration();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not change ticket plate");
            Status = exception is TicketOperationException ? exception.Message : "Couldn’t update the ticket. Please try again.";
            return false;
        }
        finally { IsBusy = false; Notify(); }
    }

    public async Task<bool> ChangeStateAsync(ParkingTicketState state)
    {
        if (ticket == null || !CanAct) return false;
        IsBusy = true;
        Notify();
        try
        {
            ticket = state switch
            {
                ParkingTicketState.Archived => await tickets.ArchiveTicketAsync(ticket.Id),
                ParkingTicketState.Active => await tickets.RestoreTicketAsync(ticket.Id),
                ParkingTicketState.Deleted => await tickets.DeleteTicketAsync(ticket.Id),
                _ => throw new ArgumentOutOfRangeException(nameof(state))
            };
            Status = state == ParkingTicketState.Active ? "Active ticket" : state == ParkingTicketState.Archived ? "Archived ticket" : "Ticket deleted";
            if (state == ParkingTicketState.Deleted) { ticket = null; Barcode = null; }
            UpdateDuration();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not change ticket state");
            Status = exception is TicketOperationException ? exception.Message : "Couldn’t update this ticket. Please try again.";
            return false;
        }
        finally { IsBusy = false; Notify(); }
    }

    private void Notify()
    {
        foreach (var name in new[] { nameof(Details), nameof(EntryDate), nameof(EntryTime), nameof(PlateNumber), nameof(SavedLocalTime), nameof(Duration), nameof(Status),
                     nameof(IsLoaded), nameof(IsBusy), nameof(CanRetry), nameof(Barcode), nameof(IsActive), nameof(IsArchived), nameof(CanAct), nameof(IsEditingPlate), nameof(CanConfirmPlate), nameof(SelectedPlate) })
            PropertyChanged?.Invoke(this, new(name));
    }
}
