using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace SuperBackend.Identity;

internal sealed class SmtpOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public bool UseStartTls { get; init; } = true;
    public string From { get; init; } = "";
    public string? Username { get; init; }
    public string? Password { get; init; }

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535 &&
        MailAddress.TryCreate(From, out _) &&
        (string.IsNullOrEmpty(Username) == string.IsNullOrEmpty(Password));
}

internal sealed record IdentityPublicOrigin(Uri Value);

internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options, IdentityPublicOrigin origin) : IEmailSender<Account>
{
    public Task SendConfirmationLinkAsync(Account user, string email, string confirmationLink) =>
        SendAsync(email, "Confirm your email", WithPublicOrigin(confirmationLink));

    public Task SendPasswordResetLinkAsync(Account user, string email, string resetLink) =>
        SendAsync(email, "Reset your password", WithPublicOrigin(resetLink));

    public Task SendPasswordResetCodeAsync(Account user, string email, string resetCode) =>
        SendAsync(email, "Password reset code", WebUtility.HtmlDecode(resetCode));

    private string WithPublicOrigin(string link)
    {
        var generated = new Uri(WebUtility.HtmlDecode(link));
        return new UriBuilder(origin.Value) { Path = generated.AbsolutePath, Query = generated.Query }.Uri.AbsoluteUri;
    }

    private async Task SendAsync(string recipient, string subject, string body)
    {
        var settings = options.Value;
        using var email = new MailMessage(settings.From, recipient, subject, body);
        using var smtp = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.UseStartTls,
            Credentials = string.IsNullOrEmpty(settings.Username)
                ? null
                : new NetworkCredential(settings.Username, settings.Password)
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await smtp.SendMailAsync(email, timeout.Token);
    }
}
