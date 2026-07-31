namespace DevStack.API.Models;

public class Shift
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool IsActive => EndTime is null;
    public int ShopId { get; set; }
}
