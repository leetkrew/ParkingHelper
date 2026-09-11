using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.ViewModels;

public sealed record PlateRow(VehiclePlate Plate, bool CanMoveUp, bool CanMoveDown)
{
    public Guid Id => Plate.Id;
    public string PlateNumber => Plate.PlateNumber;
    public string MoveUpDescription => $"Move {PlateNumber} up";
    public string MoveDownDescription => $"Move {PlateNumber} down";
    public string EditDescription => $"Edit {PlateNumber}";
    public string DeleteDescription => $"Delete {PlateNumber}";
}

public sealed class PlateEditorViewModel(IPlateService plates, ILogger<PlateEditorViewModel> logger) : INotifyPropertyChanged
{
    private bool busy;
    private bool loaded;
    private string input = string.Empty;
    private string error = string.Empty;
    private Guid? editingId;

    public ObservableCollection<PlateRow> Plates { get; } = [];
    public bool IsBusy => busy;
    public bool IsReady => loaded && !busy;
    public bool CanContinue => IsReady && Plates.Count > 0;
    public bool CanSubmit => IsReady && !string.IsNullOrWhiteSpace(Input);
    public bool IsEditing => editingId.HasValue;
    public string SubmitText => IsEditing ? "Save Changes" : "Add";
    public string Error => error;
    public bool HasError => !string.IsNullOrEmpty(error);
    public string Input
    {
        get => input;
        set { input = value; Notify(); Notify(nameof(CanSubmit)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task<bool> LoadAsync() => RunAsync(() => Task.CompletedTask);

    public Task<bool> SaveAsync() => RunAsync(async () =>
    {
        if (editingId is Guid id)
            await plates.UpdateAsync(id, Input);
        else
            await plates.AddAsync(Input);
        CancelEdit();
    });

    public Task<bool> DeleteAsync(Guid id) => RunAsync(async () =>
    {
        await plates.DeleteAsync(id);
        if (editingId == id)
            CancelEdit();
    });

    public Task<bool> MoveAsync(Guid id, int direction) => RunAsync(() => plates.MoveAsync(id, direction));

    public void Edit(PlateRow row)
    {
        if (!IsReady) return;
        editingId = row.Id;
        Input = row.PlateNumber;
        error = string.Empty;
        NotifyState();
    }

    public void CancelEdit()
    {
        editingId = null;
        Input = string.Empty;
        NotifyState();
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        if (busy) return false;
        busy = true;
        error = string.Empty;
        NotifyState();
        try
        {
            await action();
            var saved = await plates.GetPlatesAsync();
            Plates.Clear();
            for (var i = 0; i < saved.Count; i++)
                Plates.Add(new PlateRow(saved[i], i > 0, i < saved.Count - 1));
            loaded = true;
            return true;
        }
        catch (PlateOperationException exception)
        {
            error = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not load or change vehicle plates");
            error = "We couldn’t access your saved plates. Please try Refresh.";
            loaded = false;
            return false;
        }
        finally
        {
            busy = false;
            NotifyState();
        }
    }

    private void NotifyState()
    {
        foreach (var property in new[] { nameof(IsBusy), nameof(IsReady), nameof(CanContinue), nameof(CanSubmit),
                     nameof(IsEditing), nameof(SubmitText), nameof(Error), nameof(HasError) })
            Notify(property);
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
