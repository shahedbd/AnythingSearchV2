namespace AnythingSearch.Engine;

/// <summary>Engine logging, forwarded to the host app's <see cref="EngineOptions.Log"/> sink.</summary>
internal static class EngineLog
{
    public static void Log(string message) => EngineOptions.Current.Log?.Invoke(message);
}
