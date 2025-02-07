using System;
using Bannerlord.ButterLib.Logger.Extensions;
using Microsoft.Extensions.Logging;

namespace SaveCleaner;

internal static class LoggerExtensions
{
    internal static void Log(this ILogger logger, LogLevel logLevel, string message, Exception exception)
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
    
    internal static void LogAndDisplay(this ILogger logger, LogLevel logLevel, string message, Exception exception)
    {
        switch (logLevel)
        {
            case LogLevel.Trace:
                logger.LogTraceAndDisplay(exception, message);
                break;
            case LogLevel.Debug:
                logger.LogDebugAndDisplay(exception, message);
                break;
            case LogLevel.Information:
                logger.LogInformationAndDisplay(exception, message);
                break;
            case LogLevel.Warning:
                logger.LogWarningAndDisplay(exception, message);
                break;
            case LogLevel.Error:
                logger.LogErrorAndDisplay(exception, message);
                break;
            case LogLevel.Critical:
                logger.LogCriticalAndDisplay(exception, message);
                break;
            case LogLevel.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, null);
        }
    }
}