using System.Text.Json.Serialization;

namespace Microsoft.KernelMemory;

/// <summary>
/// Model representing summarized response from Summarizer API.
/// </summary>
public class SummarizedTextResponse
{
    /// <summary>
    /// Summary collection.
    /// </summary>
    [JsonPropertyName("summaries")]
    public required Summary[] Summaries { get; set; }

    public class Summary
    {
        [JsonPropertyName("abstractive_summary")]
        public required string AbstractiveSummary { get; set; }
        [JsonPropertyName("extractive_summary")]
        public required string ExtractiveSummary { get; set; }
    }
}
