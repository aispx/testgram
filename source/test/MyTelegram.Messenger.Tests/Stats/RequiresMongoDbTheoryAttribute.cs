namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// The <see cref="TheoryAttribute"/> counterpart of <see cref="RequiresMongoDbFactAttribute"/>: runs the
/// decorated data-driven test only when a real MongoDB server can be launched in the current environment,
/// and otherwise reports it as skipped rather than failed.
/// </summary>
public sealed class RequiresMongoDbTheoryAttribute : TheoryAttribute
{
    public RequiresMongoDbTheoryAttribute()
    {
        if (!EmbeddedMongoServer.MongoAvailable)
        {
            Skip = "Requires a MongoDB instance: no 'mongod' binary found on PATH " +
                   "(set STATS_TEST_MONGOD to a mongod executable to enable this integration test).";
        }
    }
}
