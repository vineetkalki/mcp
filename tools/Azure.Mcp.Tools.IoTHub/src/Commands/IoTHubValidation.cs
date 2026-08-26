// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Mcp.Core.Commands;

namespace Azure.Mcp.Tools.IoTHub.Commands;

// Shared option validation for IoT Hub commands.
internal static class IoTHubValidation
{
    public static readonly TimeSpan MaxObservationWindow = TimeSpan.FromDays(30);

    public const string InvalidHubNameError =
        "--hub-name must be 3-50 characters long and contain only letters, numbers, or hyphens, and it cannot end with a hyphen.";

    public const string InvalidLookbackError =
        "--lookback must be a positive duration no greater than 30 days, expressed as hours or ISO 8601 (for example, PT2H or P7D).";

    public const string IncompleteTimeRangeError =
        "--start-time and --end-time must be provided together.";

    public const string InvalidTimeRangeOrderError =
        "--start-time must be earlier than --end-time.";

    public const string TimeRangeTooLargeError =
        "The observation window cannot exceed 30 days.";

    public const string InvalidIntervalError =
        "--interval must be one of the Azure Monitor supported time grains: PT1M, PT5M, PT15M, PT30M, PT1H, PT6H, PT12H, or P1D.";

    // Time grains Azure Monitor accepts for the metrics query; any other value is rejected by the service.
    private static readonly TimeSpan[] s_supportedIntervals =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromDays(1),
    ];

    public static void ValidateHubName(string hubName, ValidationResult validationResult)
    {
        if (!IsValidIoTHubName(hubName))
        {
            validationResult.Errors.Add(InvalidHubNameError);
        }
    }

    public static void ValidateLookback(string? lookback, ValidationResult validationResult)
    {
        if (string.IsNullOrWhiteSpace(lookback))
        {
            return;
        }

        try
        {
            var duration = ParseLookback(lookback);
            if (duration <= TimeSpan.Zero || duration > MaxObservationWindow)
            {
                validationResult.Errors.Add(InvalidLookbackError);
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            validationResult.Errors.Add(InvalidLookbackError);
        }
    }

    public static void ValidateObservationWindow(
        string? lookback,
        DateTimeOffset? startTime,
        DateTimeOffset? endTime,
        ValidationResult validationResult)
    {
        if (startTime.HasValue != endTime.HasValue)
        {
            validationResult.Errors.Add(IncompleteTimeRangeError);
            return;
        }

        if (startTime.HasValue && endTime.HasValue)
        {
            if (startTime.Value >= endTime.Value)
            {
                validationResult.Errors.Add(InvalidTimeRangeOrderError);
            }
            else if (endTime.Value - startTime.Value > MaxObservationWindow)
            {
                validationResult.Errors.Add(TimeRangeTooLargeError);
            }

            return;
        }

        ValidateLookback(lookback, validationResult);
    }

    public static TimeSpan ParseLookback(string? lookback)
    {
        if (string.IsNullOrWhiteSpace(lookback))
        {
            return TimeSpan.FromHours(24);
        }

        return int.TryParse(lookback, out var hours)
            ? TimeSpan.FromHours(hours)
            : System.Xml.XmlConvert.ToTimeSpan(lookback);
    }

    public static void ValidateInterval(string? interval, ValidationResult validationResult)
    {
        if (string.IsNullOrWhiteSpace(interval))
        {
            return;
        }

        try
        {
            if (!s_supportedIntervals.Contains(System.Xml.XmlConvert.ToTimeSpan(interval)))
            {
                validationResult.Errors.Add(InvalidIntervalError);
            }
        }
        catch (FormatException)
        {
            validationResult.Errors.Add(InvalidIntervalError);
        }
    }

    public static bool IsValidIoTHubName(string value)
    {
        if (value.Length is < 3 or > 50 || value[^1] == '-')
        {
            return false;
        }

        foreach (var ch in value)
        {
            var isAlphaNumeric = (ch >= 'a' && ch <= 'z') ||
                                 (ch >= 'A' && ch <= 'Z') ||
                                 (ch >= '0' && ch <= '9');
            if (!isAlphaNumeric && ch != '-')
            {
                return false;
            }
        }

        return true;
    }
}
