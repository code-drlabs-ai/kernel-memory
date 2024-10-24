using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Microsoft.KernelMemory;

/// <summary>
/// Model representing request for Summarizer API.
/// </summary>
public class SummarizedTextRequest
{
    /// <summary>
    /// Text collection.
    /// </summary>
    [JsonPropertyName("texts")]
    public List<string>? Texts { get; set; }
}
