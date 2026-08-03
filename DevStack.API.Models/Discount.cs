namespace DevStack.API.Models;

// A price reduction the owner offers, applied to a whole order at checkout.
// "Specials" are the same thing with a schedule: a discount that is only live
// on certain days/times (e.g. happy hour). The POS lists live discounts and
// the server re-validates when the order is placed — the client never sets the
// discounted amount itself.
public class Discount
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "percent"; // percent | fixed
    public decimal Value { get; set; }            // 10 = 10% off, or R10 fixed
    public bool IsActive { get; set; } = true;

    // Schedule (special): null DayOfWeek = every day; null times = all day.
    public int? DayOfWeek { get; set; } // 0 = Sunday … 6 = Saturday (matches .NET + JS)
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    public int ShopId { get; set; }

    public bool IsLiveAt(DateTime now)
    {
        if (!IsActive) return false;
        if (DayOfWeek is not null && (int)now.DayOfWeek != DayOfWeek.Value) return false;
        var t = TimeOnly.FromDateTime(now);
        if (StartTime is not null && t < StartTime.Value) return false;
        if (EndTime is not null && t > EndTime.Value) return false;
        return true;
    }
}
