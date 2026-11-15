using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using SuperBackend.Jobs;
using SuperBackend.Outbox;
using SuperBackend.Platform;

namespace SuperBackend.Identity;

internal sealed class SmtpOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public bool UseStartTls { get; init; } = true;
    public string From { get; init; } = "";
    public string? Username { get; init; }
    public string? Password { get; init; }

    public bool IsValid() => Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(Host) &&
        MailAddress.TryCreate(From, out _) &&
        (string.IsNullOrEmpty(Username) == string.IsNullOrEmpty(Password));
}

internal sealed record IdentityPublicOrigin(Uri Value);
internal sealed record IdentityMailMode(bool Queued);
internal sealed record ProtectedMail(string Recipient, string Subject, string Body, DateTimeOffset ExpiresAt);

internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options, IdentityPublicOrigin origin,
    IStringLocalizer<BackendText> text, IdentityMailMode mode, IServiceScopeFactory scopes,
    IDataProtectionProvider protection, TimeProvider time) : IEmailSender<Account>
{
    internal const string JobType = "identity.mail.v1";
    private readonly IDataProtector protector = protection.CreateProtector("identity-mail-envelope-v1");

    public bool Queued => mode.Queued;

    public Task SendConfirmationLinkAsync(Account user, string email, string confirmationLink) =>
        SendAsync(email, text["ConfirmEmailSubject"], WithPublicOrigin(confirmationLink));

    public Task SendPasswordResetLinkAsync(Account user, string email, string resetLink) =>
        SendAsync(email, text["ResetPasswordSubject"], WithPublicOrigin(resetLink));

    public Task SendPasswordResetCodeAsync(Account user, string email, string resetCode) =>
        SendAsync(email, text["PasswordResetCodeSubject"], WebUtility.HtmlDecode(resetCode));

    public Task SendInvitationAsync(string email, string code, DbTransaction? transaction = null, CancellationToken ct = default) =>
        SendAsync(email, text["InviteSubject"], code, transaction, ct);

    private string WithPublicOrigin(string link)
    {
        var generated = new Uri(WebUtility.HtmlDecode(link));
        return new UriBuilder(origin.Value) { Path = generated.AbsolutePath, Query = generated.Query }.Uri.AbsoluteUri;
    }

    private async Task SendAsync(string recipient, string subject, string body, DbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        var mail = new ProtectedMail(recipient, subject, body, time.GetUtcNow().AddHours(2));
        if (!mode.Queued)
        {
            await DeliverAsync(mail, ct);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var id = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(protector.Protect(JsonSerializer.Serialize(mail)));
        if (transaction is not null)
        {
            await scope.ServiceProvider.GetRequiredService<ITransactionalOutbox>()
                .RecordAsync(transaction, new JobSubmission(id, JobType, payload), ct);
        }
        else
        {
            await scope.ServiceProvider.GetRequiredService<IDurableJobQueue>().EnqueueAsync(id, JobType, payload, ct: ct);
        }
    }

    public async Task DeliverEnvelopeAsync(string encrypted, CancellationToken ct)
    {
        var mail = JsonSerializer.Deserialize<ProtectedMail>(protector.Unprotect(encrypted))
            ?? throw new InvalidOperationException("Invalid identity mail envelope.");
        if (mail.ExpiresAt <= time.GetUtcNow())
        {
            OperationsTelemetry.Record("smtp", "expired", 0);
            return;
        }

        await DeliverAsync(mail, ct);
    }

    private async Task DeliverAsync(ProtectedMail mail, CancellationToken ct)
    {
        var settings = options.Value;
        using var email = new MailMessage(settings.From, mail.Recipient, mail.Subject, mail.Body);
        using var smtp = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.UseStartTls,
            Credentials = string.IsNullOrEmpty(settings.Username)
                ? null
                : new NetworkCredential(settings.Username, settings.Password)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await smtp.SendMailAsync(email, timeout.Token);
            OperationsTelemetry.Record("smtp", "sent", Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
        catch (Exception exception) when (exception is SmtpException || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            OperationsTelemetry.Record("smtp", "delivery-failed", Stopwatch.GetElapsedTime(started).TotalSeconds);
            throw new IdentityMailUnavailable();
        }
    }
}

internal sealed class IdentityMailHandler(SmtpEmailSender sender) : IDurableJobHandler
{
    public string Type => SmtpEmailSender.JobType;

    public Task HandleAsync(JobExecution job, CancellationToken ct) =>
        sender.DeliverEnvelopeAsync(job.Payload.GetString() ?? throw new JsonException("Missing envelope."), ct);
}

internal sealed class IdentityMailUnavailable() : Exception("Identity mail delivery failed.");

internal sealed class IdentityMailErrorHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception error, CancellationToken ct)
    {
        if (error is not IdentityMailUnavailable)
            return false;

        context.Response.Headers.RetryAfter = "30";
        await Results.Problem(statusCode: 503, title: "Mail delivery is temporarily unavailable. Request a resend.")
            .ExecuteAsync(context);
        return true;
    }
}
