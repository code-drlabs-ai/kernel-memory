﻿// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Threading;
using Microsoft.KernelMemory.Enums;
using Microsoft.KernelMemory.Models;

namespace Microsoft.KernelMemory.AI;

public interface ITextGenerator : ITextTokenizer
{
    /// <summary>
    /// Max size of the LLM attention window, considering both input and output tokens.
    /// </summary>
    public int MaxTokenTotal { get; }

    /// <summary>
    /// Generate text for the given prompt, aka generate a text completion.
    /// </summary>
    /// <param name="prompt">Prompt text</param>
    /// <param name="options">Options for the LLM request</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Text generated, returned as a stream of strings/tokens</returns>
    public IAsyncEnumerable<string> GenerateTextAsync(
        string prompt,
        TextGenerationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate text for the given prompt, aka generate a text completion and return the token usages.
    /// </summary>
    /// <param name="prompt">Prompt text</param>
    /// <param name="options">Options for the LLM request</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Text generated, returned as a stream of strings/tokens</returns>
    public IAsyncEnumerable<TextGenerationResult> GenerateTextChunkAsync(
        string prompt,
        TextGenerationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate text for the given prompt, aka generate a text completion.
    /// </summary>
    /// <param name="promptSegments">Prompt Segments</param>
    /// <param name="options">Options for the LLM request</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Text generated, returned as a stream of strings/tokens</returns>
    public IAsyncEnumerable<string> CompleteChatAsync(
        List<PromptSegment> promptSegments,
        TextGenerationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate text for the given prompt, aka generate a text completion and return the token usages.
    /// </summary>
    /// <param name="promptSegments">Prompt Segments</param>
    /// <param name="options">Options for the LLM request</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>Text generated, returned as a stream of strings/tokens</returns>
    public IAsyncEnumerable<TextGenerationResult> CompleteChatChunkAsync(
        List<PromptSegment> promptSegments,
        TextGenerationOptions options,
        CancellationToken cancellationToken = default);
}
