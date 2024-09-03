namespace Microsoft.KernelMemory.Models;
public class TextGenerationResult
{
    public TextGenerationResult(string generatedText, int completionTokens, int promptTokens, int totalTokens)
    {
        this.GeneratedText = generatedText;
        this.CompletionTokens = completionTokens;
        this.PromptTokens = promptTokens;
        this.TotalTokens = totalTokens;
    }

    /// <summary> Generated text response.</summary>
    public string GeneratedText { get; }

    /// <summary> The number of tokens it took to generate the response.</summary>
    public int CompletionTokens { get; }

    /// <summary> The number of tokens in the provided prompts for the completions request.</summary>
    public int PromptTokens { get; }

    /// <summary> The total number of tokens processed for the completions request and response.</summary>
    public int TotalTokens { get; }
}
