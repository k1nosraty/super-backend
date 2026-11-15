using System.ComponentModel.DataAnnotations;

namespace SuperBackend.Federation;

internal sealed record FederationSettings(Uri Authority, string Issuer, string ClientId, string ClientSecret,
    string PublicOrigin, string? RequiredAcr);

public sealed record ExternalAccountLink(
    [Required, StringLength(128)] string UserId,
    [Required, StringLength(255)] string Subject);
