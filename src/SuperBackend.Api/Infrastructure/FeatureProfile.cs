namespace SuperBackend.Api;

internal sealed record FeatureProfile(bool Notes, bool Federation, bool Tenancy, bool Jobs, bool Outbox, bool PublishNoteJobs, bool QueueMail)
{
    public static FeatureProfile Read(IConfiguration configuration)
    {
        var profile = new FeatureProfile(configuration.GetValue<bool>("Modules:Notes"),
            configuration.GetValue<bool>("Modules:Federation"), configuration.GetValue<bool>("Modules:Tenancy"),
            configuration.GetValue<bool>("Modules:Jobs"), configuration.GetValue<bool>("Modules:Outbox"),
            configuration.GetValue<bool>("Notes:PublishCreatedJobs"), configuration.GetValue<bool>("Identity:QueueMail"));
        if (profile.Outbox && !profile.Jobs)
            throw new InvalidOperationException("Modules:Outbox requires Modules:Jobs.");
        if (profile.PublishNoteJobs && (!profile.Notes || !profile.Jobs || !profile.Outbox))
            throw new InvalidOperationException("Notes:PublishCreatedJobs requires Modules:Notes, Modules:Jobs and Modules:Outbox.");
        if (profile.QueueMail && !profile.Outbox)
            throw new InvalidOperationException("Identity:QueueMail requires Modules:Outbox and Modules:Jobs.");
        return profile;
    }
}
