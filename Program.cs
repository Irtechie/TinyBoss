using Avalonia;
using Avalonia.Controls;
using TinyBoss;
using TinyBoss.Core;
using TinyBoss.Handlers;
using TinyBoss.Platform.Windows;
using TinyBoss.Protocol;
using TinyBoss.Voice;

if (ElevationRelauncher.TryRelaunchFromInstalledTask(StartupLog))
    return;

// ── Single-instance mutex ────────────────────────────────────────────────────
using var mutex = new Mutex(true, @"Global\TinyBoss", out bool isFirst);
if (!isFirst)
{
    // TODO: Signal existing instance via named pipe activation
    Console.Error.WriteLine("TinyBoss is already running.");
    return;
}

ThreadPool.SetMinThreads(16, 16);

// ── Global crash protection ─────────────────────────────────────────────────
var crashLog = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Programs", "TinyBoss", "crash.log");

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var msg = e.ExceptionObject is Exception ex
        ? $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
        : e.ExceptionObject?.ToString();
    try { File.AppendAllText(crashLog, $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UNHANDLED: {msg}\n"); }
    catch { }
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    e.SetObserved(); // prevent crash from unobserved task exceptions
    try { File.AppendAllText(crashLog, $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] TASK_UNOBSERVED: {e.Exception.Message}\n{e.Exception.StackTrace}\n"); }
    catch { }
};

// ── Build Kestrel host (named pipe + legacy TCP) ────────────────────────────
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenNamedPipe("TinyBoss");
    k.ListenLocalhost(8033);   // Legacy TCP — remove after PitBoss migrates to named pipe
});

builder.Services.AddSingleton(TinyBossConfig.Load());
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<SpawnHandler>();
builder.Services.AddSingleton<InjectHandler>();
builder.Services.AddSingleton<WindowInjectHandler>();
builder.Services.AddSingleton<KillHandler>();
builder.Services.AddSingleton<IntrospectHandler>();
builder.Services.AddSingleton<SignalHandler>();
builder.Services.AddSingleton<AnswerUserHandler>();
builder.Services.AddSingleton<RenameHandler>();

// Voice input pipeline
builder.Services.AddSingleton<HotKeyListener>();
builder.Services.AddSingleton<AudioCapture>();
builder.Services.AddSingleton<HallucinationGuard>();
builder.Services.AddSingleton<WhisperTranscriber>();
builder.Services.AddSingleton<TextInjector>();
builder.Services.AddSingleton<VoiceController>();

// Tiling pipeline
builder.Services.AddSingleton<TilingCoordinator>();
builder.Services.AddSingleton<DragWatcher>();

var app = builder.Build();
app.UseWebSockets();

var authToken =
    Environment.GetEnvironmentVariable("PITBOSS_AUTH_TINYBOSS")
    ?? Environment.GetEnvironmentVariable("PITBOSS_AUTH_TinyBoss")
    ?? Environment.GetEnvironmentVariable("PITBOSS_AUTH_KITTENHERDER")
    ?? string.Empty;
if (string.IsNullOrEmpty(authToken))
    app.Logger.LogWarning("No TinyBoss auth token set (PITBOSS_AUTH_TINYBOSS or PITBOSS_AUTH_KITTENHERDER) — running in open dev mode");

// ── Single multiplexed WebSocket endpoint (same protocol, transport changed) ──
app.MapGet("/ws", async (HttpContext ctx,
    SpawnHandler spawn, InjectHandler inject, KillHandler kill,
    WindowInjectHandler windowInject,
    IntrospectHandler introspect, SignalHandler signal, AnswerUserHandler answerUser,
    RenameHandler rename,
    ILogger<Program> logger) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    logger.LogInformation("KH: Client connected");

    var handler = new MessageHandler(
        spawn, inject, windowInject, kill, introspect, signal, answerUser, rename,
        ctx.RequestServices.GetRequiredService<ILogger<MessageHandler>>(),
        authToken);

    await handler.RunConnectionAsync(ws, ctx.RequestAborted);
    logger.LogInformation("KH: Client disconnected");
});

app.MapGet("/health", (SessionRegistry registry, TilingCoordinator tiling) => Results.Ok(new
{
    status = "ok",
    elevated = ElevationRelauncher.IsCurrentProcessElevated(),
    sessions = registry.All().Count(),
    windows = tiling.GetAllSnapshots().Sum(s => s.Slots.Count),
    transport = "pipe:TinyBoss + tcp:8033"
}));

app.MapGet("/api/voice/status", (VoiceController voice) => Results.Ok(voice.GetVoiceStatus()));

// ── Bridge DI to Avalonia ────────────────────────────────────────────────────
TinyBossServices.Initialize(app.Services);

// ── Start Kestrel (non-blocking) ─────────────────────────────────────────────
app.StartAsync().GetAwaiter().GetResult();
app.Logger.LogInformation("TinyBoss listening on pipe:TinyBoss + tcp:127.0.0.1:8033");

try
{
    // ── Start Avalonia on main thread (blocks until user quits) ──────────────
    AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace()
        .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
}
finally
{
    // ── Graceful shutdown after Avalonia exits ────────────────────────────────
    app.Logger.LogInformation("TinyBoss shutting down...");
    try { app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
    catch (Exception ex) { StartupLog($"Host stop failed: {ex.Message}"); }

    try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    catch (Exception ex) { StartupLog($"Host dispose failed: {ex.Message}"); }
}

static void StartupLog(string message)
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "TinyBoss");
        Directory.CreateDirectory(dir);
        File.AppendAllText(
            Path.Combine(dir, "startup.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
    }
    catch { }
}

