namespace Microsoft.KernelMemory.Models;
public class CompletionUsage
{
    public CompletionUsage(int completionTokens = 0, int promptTokens = 0, int totalTokens = 0)
    {
        this.CompletionTokens = completionTokens;
        this.PromptTokens = promptTokens;
        this.TotalTokens = totalTokens;
    }

    public int CompletionTokens { get; set; }
    /// <summary> The number of tokens in the provided prompts for the completions request. </summary>
    public int PromptTokens { get; set; }
    /// <summary> The total number of tokens processed for the completions request and response. </summary>
    public int TotalTokens { get; set; }
}
