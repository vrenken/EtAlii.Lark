namespace EtAlii.Lark.Tests;

/// <summary>Tests that change global state (the logger, the cache file system) run in isolation.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalStateCollection
{
    public const string Name = "Global state";
}
