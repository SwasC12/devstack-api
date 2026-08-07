namespace DevStack.API.Models;

// An uploaded APK release. History is kept (never overwritten): the newest
// release is marked current, older ones remain for rollback. The APK binary
// lives in the DB so it survives host restarts/disk wipes.
public class AppRelease
{
    public int Id { get; set; }
    public string Version { get; set; } = string.Empty; // e.g. "1.3.0"
    public byte[] ApkData { get; set; } = [];           // the APK binary
    public long SizeBytes { get; set; }
    public string ReleaseNotes { get; set; } = string.Empty;
    public bool IsRequired { get; set; }                // force update gate
    public bool IsCurrent { get; set; }                 // newest published release
    public DateTime CreatedAtUtc { get; set; }
}
