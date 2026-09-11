namespace ParkingHelper.Core.Models;

public static class TicketDuration
{
    public static TimeSpan Calculate(ParkingTicket ticket, DateTime utcNow)
    {
        var end = ticket.State == ParkingTicketState.Archived ? ticket.ArchivedUtc ?? ticket.CreatedUtc : utcNow;
        var elapsed = end - ticket.CreatedUtc;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    public static string Format(ParkingTicket ticket, DateTime utcNow)
    {
        var elapsed = Calculate(ticket, utcNow);
        var time = $"{elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        return elapsed.Days > 0 ? $"{elapsed.Days}d {time}" : time;
    }
}
