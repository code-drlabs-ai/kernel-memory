namespace Microsoft.KernelMemory.Models;
public class CompletionUsage
{
    public CompletionUsage(int completionTokens, int promptTokens, int totalTokens)
    {
        this.CompletionTokens = completionTokens;
        this.PromptTokens = promptTokens;
        this.TotalTokens = totalTokens;
    }

    public int CompletionTokens { get; }
    /// <summary> The number of tokens in the provided prompts for the completions request. </summary>
    public int PromptTokens { get; }
    /// <summary> The total number of tokens processed for the completions request and response. </summary>
    public int TotalTokens { get; }
}
