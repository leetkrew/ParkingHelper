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
            foreach (var plate in choices)
            {
                var choice = new TicketPlateChoice(plate);
                choice.SetSelected(plate.Id == ticket!.VehiclePlateId);
                PlateChoices.Add(choice);
            }
            selectedPlate = choices.FirstOrDefault(plate => plate.Id == ticket!.VehiclePlateId);
            IsEditingPlate = true;
            Status = choices.Count == 0 ? "No saved plates are available." : "Choose a plate, then confirm the change.";
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
        if (plateId == ticket.VehiclePlateId) { CancelEditPlate(); return true; }
        IsBusy = true;
        Notify();
        try
        {
            ticket = await tickets.ChangeTicketPlateAsync(ticket.Id, plateId);
            IsEditingPlate = false;
            Status = "Ticket plate updated";
            UpdateDuration();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not change ticket plate");
            Status = exception is TicketOperationException ? exception.Message : "Couldn’t update the plate. Please try again.";
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
        foreach (var name in new[] { nameof(PlateNumber), nameof(SavedLocalTime), nameof(Duration), nameof(Status),
                     nameof(IsLoaded), nameof(IsBusy), nameof(CanRetry), nameof(Barcode), nameof(IsActive), nameof(IsArchived), nameof(CanAct), nameof(IsEditingPlate), nameof(CanConfirmPlate), nameof(SelectedPlate) })
            PropertyChanged?.Invoke(this, new(name));
    }
}
