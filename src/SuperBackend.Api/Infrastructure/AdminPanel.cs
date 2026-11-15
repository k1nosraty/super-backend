namespace SuperBackend.Api;

internal static class AdminPanel
{
    public static void MapAdminPanel(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Modules:AdminPanel"))
            return;

        app.MapGet("/admin", () => Results.Redirect("/admin/index.html")).AllowAnonymous().ExcludeFromDescription();
        app.MapStaticAssets().AllowAnonymous();
    }
}
