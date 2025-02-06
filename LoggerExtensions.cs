using System;
using Microsoft.Extensions.Logging;

namespace SaveCleaner;

public static class LoggerExtensions
{
    public static void Log(this ILogger logger, LogLevel logLevel, string message, Exception exception)
    {
        switch (logLevel)
        {
            case LogLevel.Trace:
                logger.LogTrace(exception, message);
                break;
            case LogLevel.Debug:
                logger.LogDebug(exception, message);
                break;
            case LogLevel.Information:
                logger.LogInformation(exception, message);
                break;
            case LogLevel.Warning:
                logger.LogWarning(exception, message);
                break;
            case LogLevel.Error:
                logger.LogError(exception, message);
                break;
            case LogLevel.Critical:
                logger.LogCritical(exception, message);
                break;
            case LogLevel.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, null);
        }
    }
}