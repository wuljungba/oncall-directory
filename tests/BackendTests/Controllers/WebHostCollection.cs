namespace BackendTests.Controllers;

/// <summary>
/// Test classes that boot the real app via <c>WebApplicationFactory</c> share one
/// collection so xUnit runs them sequentially.
///
/// They all point at the same file-backed SQLite dev database. Started in parallel on a
/// machine where that file does not exist yet (a clean CI checkout), several hosts race
/// inside <c>EnsureCreatedAsync</c> and one loses with a SQLite error at startup.
/// </summary>
[CollectionDefinition(WebHostCollection.Name, DisableParallelization = true)]
public class WebHostCollection
{
    public const string Name = "web-host";
}
