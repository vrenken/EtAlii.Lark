namespace EtAlii.Lark;

/// <summary>Severity of a log message.</summary>
public enum LarkLogLevel
{
    Debug = 10,
    Info = 20,
    Warning = 30,
    Error = 40,
    Critical = 50,
}

/// <summary>
/// Minimal logging facility (Lark's <c>logger</c>). By default nothing is logged; set <see cref="Level"/> and
/// optionally <see cref="Sink"/> to receive messages.
/// </summary>
public static class LarkLog
{
    /// <summary>The minimal level of messages that are emitted. Defaults to <see cref="LarkLogLevel.Critical"/>.</summary>
    public static LarkLogLevel Level { get; set; } = LarkLogLevel.Critical;

    /// <summary>Receives the emitted messages. Defaults to writing to <see cref="Console.Error"/>.</summary>
    public static Action<LarkLogLevel, string> Sink { get; set; } = (_, message) => Console.Error.WriteLine(message);

    public static bool IsEnabled(LarkLogLevel level) => level >= Level;

    public static void Log(LarkLogLevel level, string message)
    {
        if (IsEnabled(level)) Sink(level, message);
    }

    public static void Debug(string message) => Log(LarkLogLevel.Debug, message);

    public static void Info(string message) => Log(LarkLogLevel.Info, message);

    public static void Warning(string message) => Log(LarkLogLevel.Warning, message);

    public static void Error(string message) => Log(LarkLogLevel.Error, message);
}
