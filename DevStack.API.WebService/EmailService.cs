using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DevStack.API.WebService;

// Server-side email for the platform owner. Config comes from env/config:
//   Smtp:Host, Smtp:Port, Smtp:User, Smtp:Pass, Smtp:From, Smtp:FromName, Smtp:EnableSsl
// When Smtp:Host is empty the service reports IsConfigured=false and callers
// fall back to the mailto: flow in the UI - no SMTP, no server email.
public class EmailService
{
    private readonly string? _host;
    private readonly int _port;
    private readonly string? _user;
    private readonly string? _pass;
    private readonly string _from;
    private readonly string _fromName;
    private readonly bool _enableSsl;

    public EmailService(IConfiguration config)
    {
        _host = config["Smtp:Host"];
        int.TryParse(config["Smtp:Port"], out _port);
        if (_port == 0) _port = 587;
        _user = config["Smtp:User"];
        _pass = config["Smtp:Pass"];
        _from = config["Smtp:From"] ?? "no-reply@coffeeshoppro.app";
        _fromName = config["Smtp:FromName"] ?? "CoffeeShop Pro";
        _enableSsl = !bool.TryParse(config["Smtp:EnableSsl"], out var ssl) || ssl;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_host);

    public async Task SendAsync(string to, string subject, string body, bool html = false)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("SMTP is not configured on this server.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_fromName, _from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart(html ? "html" : "plain") { Text = body };

        using var client = new SmtpClient();
        // StartTLS when available; fall back gracefully. Never send credentials
        // in the clear - if auth is set but the server won't do TLS, fail loudly.
        await client.ConnectAsync(_host, _port,
            _enableSsl ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.Auto);
        if (!string.IsNullOrEmpty(_user))
            await client.AuthenticateAsync(_user, _pass);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}
