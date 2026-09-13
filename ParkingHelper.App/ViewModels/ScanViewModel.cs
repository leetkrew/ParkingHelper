using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.ViewModels;

public sealed class ScanPlateRow(VehiclePlate plate) : INotifyPropertyChanged
{
    private bool selected;
    public VehiclePlate Plate { get; } = plate;
    public Guid Id => Plate.Id;
    public string PlateNumber => Plate.PlateNumber;
    public string Label => IsSelected ? $"✓ {PlateNumber}" : PlateNumber;
    public string Description => IsSelected ? $"{PlateNumber}, selected" : $"Select {PlateNumber}";
    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            foreach (var name in new[] { nameof(IsSelected), nameof(Label), nameof(Description) })
                PropertyChanged?.Invoke(this, new(name));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ScanViewModel(
    IPlateService plates, ISelectedPlatePreference preference, ITicketService tickets,
    ILogger<ScanViewModel> logger) : INotifyPropertyChanged
{
    private ScanResult? handledScan;
    public ObservableCollection<ScanPlateRow> Plates { get; } = [];
    public VehiclePlate? SelectedPlate { get; private set; }
    public bool CanScan => SelectedPlate != null;
    public bool IsSaving { get; private set; }
    public bool SaveSucceeded { get; private set; }
    public bool HasSaveError { get; private set; }
    public string Feedback { get; private set; } = "";
    public string FeedbackDetail { get; private set; } = "";
    public string PlateStatus { get; private set; } = "Loading your plates…";
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task<bool> LoadAsync()
    {
        try
        {
            var saved = (await plates.GetPlatesAsync()).OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToArray();
            Guid? preferred;
            try { preferred = preference.SelectedPlateId; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not read selected plate preference");
                preferred = SelectedPlate?.Id;
            }
            Plates.Clear();
            foreach (var plate in saved) Plates.Add(new ScanPlateRow(plate));
            SetSelection(saved.FirstOrDefault(p => p.Id == preferred) ?? saved.FirstOrDefault());
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load scanner plate buttons");
            Plates.Clear();
            SelectedPlate = null;
            PlateStatus = "Couldn’t load plates. Tap Reload plates to try again.";
            Notify();
            return false;
        }
    }

    public void SelectPlate(Guid id)
    {
        var selected = Plates.FirstOrDefault(p => p.Id == id)?.Plate;
        if (selected != null) SetSelection(selected);
    }

    private void SetSelection(VehiclePlate? selected)
    {
        SelectedPlate = selected;
        foreach (var row in Plates) row.IsSelected = row.Id == selected?.Id;
        PlateStatus = selected == null ? "Add a plate to start scanning." : $"Selected: {selected.PlateNumber}";
        try { preference.SelectedPlateId = selected?.Id; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not remember selected plate");
            PlateStatus += " · Couldn’t remember this selection. Tap the plate to retry.";
        }
        Notify();
    }

    public async Task<TicketCreationResult?> SaveScanAsync(ScanResult scan)
    {
        if (IsSaving || (handledScan != null && ReferenceEquals(handledScan, scan))) return null;
        handledScan = scan;
        // Capture the selection synchronously, before the first await. Plate buttons remain usable.
        var selected = SelectedPlate;
        IsSaving = true;
        SaveSucceeded = false;
        HasSaveError = false;
        Feedback = "Saving ticket…";
        FeedbackDetail = selected?.PlateNumber ?? "";
        Notify();
        try
        {
            if (selected == null) throw new TicketOperationException("Select a saved plate before scanning.");
            var outcome = await tickets.CreateActiveTicketAsync(scan, selected);
            SaveSucceeded = outcome.Created;
            Feedback = outcome.Created ? "✓ Ticket saved" : "Ticket already saved";
            var ticket = outcome.Ticket;
            var number = ticket.PlateNumberSnapshot
                ?? Plates.FirstOrDefault(p => p.Id == ticket.VehiclePlateId)?.PlateNumber ?? "Saved plate";
            FeedbackDetail = $"{number} • {ticket.CreatedUtc.ToLocalTime():h:mm tt}";
            return outcome;
        }
        catch (TicketOperationException exception)
        {
            HasSaveError = true;
            Feedback = "Ticket not saved";
            FeedbackDetail = exception.Message;
            return null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not save scanned ticket");
            HasSaveError = true;
            Feedback = "Ticket not saved";
            FeedbackDetail = "Couldn’t save to this device. Please scan again.";
            return null;
        }
        finally
        {
            IsSaving = false;
            Notify();
        }
    }

    public void ReportPreviewFailure()
    {
        Feedback = "Ticket saved";
        FeedbackDetail = "Couldn’t open the preview. Tap Open saved ticket to retry.";
        Notify();
    }

    public void ClearFeedback()
    {
        if (IsSaving) return;
        Feedback = "";
        FeedbackDetail = "";
        SaveSucceeded = false;
        HasSaveError = false;
        Notify();
    }

    private void Notify()
    {
        foreach (var name in new[] { nameof(SelectedPlate), nameof(CanScan), nameof(IsSaving),
                     nameof(SaveSucceeded), nameof(HasSaveError), nameof(Feedback), nameof(FeedbackDetail), nameof(PlateStatus) })
            PropertyChanged?.Invoke(this, new(name));
    }
}
