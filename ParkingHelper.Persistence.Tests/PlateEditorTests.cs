using Microsoft.Extensions.Logging.Abstractions;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class PlateEditorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Editor.Tests", Guid.NewGuid().ToString());
    private PlateEditorViewModel Model() => new(new PlateService(
        new SqliteParkingRepository(Path.Combine(directory, "plates.db3")), TimeProvider.System),
        NullLogger<PlateEditorViewModel>.Instance);

    [Fact]
    public async Task ContinueIsGatedBySuccessfulSavedStateAndLastDeletion()
    {
        var model = Model();
        Assert.False(model.CanContinue);
        Assert.True(await model.LoadAsync());
        Assert.False(model.CanContinue);
        model.Input = "abc123";
        Assert.True(model.CanSubmit);
        Assert.False(model.CanContinue); // Typing alone must not enable Continue.
        Assert.True(await model.SaveAsync());
        Assert.True(model.CanContinue);
        Assert.Equal("ABC123", Assert.Single(model.Plates).PlateNumber);
        Assert.False(model.CanSubmit);
        Assert.True(await model.DeleteAsync(model.Plates[0].Id));
        Assert.False(model.CanContinue);
    }

    [Fact]
    public async Task DuplicateEditKeepsInputAndCancelDoesNotWrite()
    {
        var model = Model();
        await model.LoadAsync();
        model.Input = "ABC123";
        await model.SaveAsync();
        model.Input = "MOTOR1";
        await model.SaveAsync();
        var id = model.Plates[1].Id;
        model.Edit(model.Plates[1]);
        model.Input = " abc123 ";
        Assert.False(await model.SaveAsync());
        Assert.True(model.IsEditing);
        Assert.True(model.HasError);
        Assert.Equal(" abc123 ", model.Input);
        Assert.Equal("MOTOR1", model.Plates[1].PlateNumber);
        model.CancelEdit();
        await model.LoadAsync();
        Assert.False(model.IsEditing);
        Assert.Equal(id, model.Plates[1].Id);
        Assert.Equal("MOTOR1", model.Plates[1].PlateNumber);
        Assert.False(model.Plates[0].CanMoveUp);
        Assert.False(model.Plates[1].CanMoveDown);
        await model.MoveAsync(id, -1);
        Assert.Equal(id, model.Plates[0].Id);
        Assert.False(model.Plates[0].CanMoveUp);
        Assert.True(model.Plates[0].CanMoveDown);
    }

    [Fact]
    public async Task StorageFailureCanBeRetriedWithoutAllowingContinue()
    {
        var service = new UnavailablePlateService();
        var model = new PlateEditorViewModel(service, NullLogger<PlateEditorViewModel>.Instance);
        Assert.False(await model.LoadAsync());
        Assert.True(model.HasError);
        Assert.False(model.IsBusy);
        Assert.False(model.CanContinue);
        Assert.False(model.IsReady);
        service.Available = true;
        Assert.True(await model.LoadAsync());
        Assert.False(model.HasError);
        Assert.True(model.IsReady);
        Assert.False(model.CanContinue);
    }

    private sealed class UnavailablePlateService : IPlateService
    {
        public bool Available { get; set; }
        public Task<IReadOnlyList<VehiclePlate>> GetPlatesAsync() => Available
            ? Task.FromResult<IReadOnlyList<VehiclePlate>>([])
            : Task.FromException<IReadOnlyList<VehiclePlate>>(new IOException("Unavailable"));
        public Task<bool> NeedsSetupAsync() => throw new NotSupportedException();
        public Task<VehiclePlate> AddAsync(string number) => throw new NotSupportedException();
        public Task UpdateAsync(Guid id, string number) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task MoveAsync(Guid id, int direction) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
