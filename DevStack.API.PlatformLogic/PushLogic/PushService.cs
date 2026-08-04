using DevStack.API.Models;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevStack.API.PlatformLogic.PushLogic;

// Sends Firebase Cloud Messaging pushes. Fails SOFT: when no Firebase service
// account is configured (local dev, or the secret isn't set yet) the send is
// skipped with a log line - the in-app notification row still exists, so the
// feature degrades gracefully instead of crashing.
public interface IPushService
{
    // Returns true when delivered, false when the token is dead (caller should
    // delete it), null when Firebase isn't configured / send skipped.
    Task<bool?> SendAsync(PushToken token, string title, string body, string type, int? notificationId = null);
}

public class PushService : IPushService
{
    private readonly IConfiguration _config;
    private readonly ILogger<PushService> _logger;
    private static readonly object _initLock = new();
    private static bool _initTried;
    private static bool _ready;

    public PushService(IConfiguration config, ILogger<PushService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<bool?> SendAsync(PushToken token, string title, string body, string type, int? notificationId = null)
    {
        try
        {
            if (!EnsureInitialized()) return null;

            await FirebaseMessaging.DefaultInstance.SendAsync(new Message
            {
                Token = token.Token,
                Notification = new FirebaseAdmin.Messaging.Notification
                {
                    Title = title,
                    Body = body
                },
                Data = new Dictionary<string, string>
                {
                    ["type"] = type,
                    ["notificationId"] = notificationId?.ToString() ?? ""
                },
                Android = new AndroidConfig { Priority = Priority.High }
            });
            return true;
        }
        catch (FirebaseMessagingException ex)
        {
            // Unregistered / invalid token: FCM says this device is gone.
            if (ex.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument or MessagingErrorCode.SenderIdMismatch)
            {
                _logger.LogWarning("FCM: dropping dead token {token}", token.Token[..Math.Min(12, token.Token.Length)]);
                return false;
            }
            _logger.LogError(ex, "FCM send failed");
            return true; // transient error - keep the token, retry next broadcast
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FCM send failed (non-FCM error)");
            return true;
        }
    }

    private bool EnsureInitialized()
    {
        if (_ready) return true;
        lock (_initLock)
        {
            if (_ready) return true;
            if (_initTried) return _ready;

            _initTried = true;
            var json = _config["Firebase:ServiceAccountJson"];
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Firebase service account not configured - push sends skipped (in-app notifications still work).");
                return false;
            }

            try
            {
                if (FirebaseApp.DefaultInstance is null)
                {
                    FirebaseApp.Create(new AppOptions
                    {
                        Credential = GoogleCredential.FromJson(json),
                        ProjectId = ParseProjectId(json)
                    });
                }
                _ready = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Firebase init failed - push sends disabled");
            }
            return _ready;
        }
    }

    private static string? ParseProjectId(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("project_id", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }
}
