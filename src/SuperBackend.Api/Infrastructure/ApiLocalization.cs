using Microsoft.AspNetCore.Localization;

namespace SuperBackend.Api;

internal static class ApiLocalization
{
    public static void AddApiLocalization(this IServiceCollection services)
    {
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.SetDefaultCulture("en").AddSupportedCultures("en", "fa").AddSupportedUICultures("en", "fa");
            options.RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()];
            options.ApplyCurrentCultureToResponseHeaders = true;
        });
    }
}
