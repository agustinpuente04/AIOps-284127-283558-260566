namespace InstrumentationInterface
{
    public interface IStructuredLogger
    {
        void LogInformation(string message, Dictionary<string, object>? metadata = null);
        void LogWarning(string message, Exception? exception = null, Dictionary<string, object>? metadata = null);
        void LogError(string message, Exception? exception = null, Dictionary<string, object>? metadata = null);
    }
}

