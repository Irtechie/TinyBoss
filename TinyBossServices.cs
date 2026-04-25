namespace KittenHerder;

/// <summary>
/// Static service locator bridging Kestrel DI and Avalonia App.
/// Initialized before Avalonia starts; used by App.axaml.cs to access SessionRegistry.
/// </summary>
public static class TinyBossServices
{
    private static IServiceProvider? _provider;

    public static IServiceProvider Provider =>
        _provider ?? throw new InvalidOperationException("TinyBossServices not initialized — Kestrel must start first");

    public static void Initialize(IServiceProvider provider) => _provider = provider;
}
