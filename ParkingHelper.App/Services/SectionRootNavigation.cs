namespace ParkingHelper.App.Services;

// Only primary-section activation uses this policy. Back/pop navigation never calls it.
public sealed class SectionRootNavigation
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task OpenAsync(string section, Func<Task> popToRoot, Func<Task> selectActiveTickets)
    {
        await gate.WaitAsync();
        try
        {
            await popToRoot();
            if (section == "tickets") await selectActiveTickets();
        }
        finally { gate.Release(); }
    }
}
