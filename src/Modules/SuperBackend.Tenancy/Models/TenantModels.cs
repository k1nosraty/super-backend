using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SuperBackend.Tenancy;

[JsonConverter(typeof(JsonStringEnumConverter<TenantAccess>))]
public enum TenantAccess { Reader, Editor }

public sealed record TenantSummary(Guid Id, string Name);
public sealed record NewTenant([Required, StringLength(100)] string Name);
public sealed record TenantMembership([Required, EnumDataType(typeof(TenantAccess))] TenantAccess? Access);
public sealed record DocumentInput([Required, StringLength(120)] string Title,
    [Required(AllowEmptyStrings = true), StringLength(4000)] string Body);
public sealed record TenantDocument(Guid Id, string Title, string Body, Guid Version);
public sealed record TenantStatus([Required] bool? Active, [Required, StringLength(500)] string Reason);
public enum DocumentUpdate { Updated, Missing, Stale }
