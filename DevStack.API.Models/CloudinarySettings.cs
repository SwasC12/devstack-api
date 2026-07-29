namespace DevStack.API.Models;

// Populated from the "Cloudinary" section of appsettings.json.
public class CloudinarySettings
{
    public string CloudName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
}
