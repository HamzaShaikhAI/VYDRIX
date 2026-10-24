using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vydrix.Core;

namespace Vydrix.Cache;

public sealed record StorageRecommendation(
    string Title,
    string Description,
    string Impact,
    string ActionableSuggestion);

/// <summary>
/// Storage Time Machine: Local privacy-first diagnostic timeline and storage advisor.
/// Answers why operations were delayed, explains cache churn, and offers safe actionable suggestions.
/// </summary>
public sealed class StorageTimeMachine
{
    private readonly object _lock = new();
    private readonly List<TimeMachineEvent> _events = new();
    private const int MaxHistoryEvents = 500;

    private static readonly Regex SensitivePattern = new(
        @"(access_token|refresh_token|password|client_secret|bearer|authorization)=([^\s&""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void RecordEvent(string category, string summary, string details, string? gameId = null, long? impactBytes = null)
    {
        var redactedSummary = Redact(summary);
        var redactedDetails = Redact(details);

        lock (_lock)
        {
            _events.Add(new TimeMachineEvent(
                DateTime.Now,
                category,
                redactedSummary,
                redactedDetails,
                gameId,
                impactBytes));

            if (_events.Count > MaxHistoryEvents)
            {
                _events.RemoveRange(0, _events.Count - MaxHistoryEvents);
            }
        }
    }

    public IReadOnlyList<TimeMachineEvent> GetRecentEvents(int maxCount = 50)
    {
        lock (_lock)
        {
            return _events
                .OrderByDescending(e => e.Timestamp)
                .Take(maxCount)
                .ToList();
        }
    }

    /// <summary>
    /// Analyzes history and generates safe storage recommendations.
    /// </summary>
    public IReadOnlyList<StorageRecommendation> GetRecommendations(long currentUsageBytes, long budgetBytes)
    {
        var list = new List<StorageRecommendation>();
        lock (_lock)
        {
            var evictions = _events.Where(e => e.Category == "Eviction").ToList();
            if (evictions.Count > 10)
            {
                list.Add(new StorageRecommendation(
                    "High Cache Churn Detected",
                    $"Observed {evictions.Count} eviction cycles recently. Working set exceeds active cache budget.",
                    "Frequent cloud refetches cause micro-stutters during game asset streaming.",
                    "Consider switching to 'Balanced' profile or raising cache budget by 1–2 GB."));
            }

            var netDips = _events.Where(e => e.Category == "NetworkBottleneck").ToList();
            if (netDips.Any())
            {
                list.Add(new StorageRecommendation(
                    "Cloud Network Congestion",
                    $"{netDips.Count} requests suffered from elevated round-trip times to Google Drive.",
                    "Read requests were throttled to prevent timeout cascades.",
                    "Enable 'Latency Shield' and keep 'Coalescing Read Pipeline' active to bundle requests."));
            }

            if (currentUsageBytes > budgetBytes * 0.9)
            {
                list.Add(new StorageRecommendation(
                    "Cache Near Capacity Limit",
                    $"Current usage is {ByteSize.Format(currentUsageBytes)} of {ByteSize.Format(budgetBytes)} budget.",
                    "Upcoming reads will require synchronous eviction of older data.",
                    "Run 'Run safe cleanup now' or close completed game sessions to release transient files."));
            }
        }

        if (list.Count == 0)
        {
            list.Add(new StorageRecommendation(
                "Optimal Storage Health",
                "Cache budget, in-flight capacity headroom, and network latency are operating within target parameters.",
                "Near-physical-SSD read responsiveness active.",
                "No action required."));
        }

        return list;
    }

    /// <summary>
    /// Immediately clears all stored diagnostic events from memory.
    /// </summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }

    private static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return SensitivePattern.Replace(input, "$1=[REDACTED]");
    }
}
