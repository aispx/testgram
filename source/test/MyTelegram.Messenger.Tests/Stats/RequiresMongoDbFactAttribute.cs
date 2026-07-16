namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 10.3 — an <see cref="FactAttribute"/> that runs the decorated test only when a
/// real MongoDB server can be launched in the current environment, and otherwise reports the test as
/// skipped (rather than failed) with an explanatory reason.
///
/// <para>The integration test requires a real/embedded MongoDB. This repository has no embedded-Mongo
/// NuGet harness, so the test drives the <c>mongod</c> binary present on the machine. When that binary is
/// absent (e.g. CI without MongoDB installed and no <c>STATS_TEST_MONGOD</c> override) the test is skipped
/// cleanly instead of failing.</para>
/// </summary>
public sealed class RequiresMongoDbFactAttribute : FactAttribute
{
    public RequiresMongoDbFactAttribute()
    {
        if (!EmbeddedMongoServer.MongoAvailable)
        {
            Skip = "Requires a MongoDB instance: no 'mongod' binary found on PATH " +
                   "(set STATS_TEST_MONGOD to a mongod executable to enable this integration test).";
        }
    }
}
