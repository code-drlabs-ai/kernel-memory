//// Copyright (c) Microsoft. All rights reserved.

//using System;
//using System.ClientModel;
//using System.ClientModel.Primitives;
//using System.Collections.Generic;
//using System.Diagnostics.CodeAnalysis;
//using System.Linq;
//using System.Net.Http;
//using System.Runtime.CompilerServices;
//using System.Threading;
//using Microsoft.Extensions.Logging;
//using Microsoft.KernelMemory.Diagnostics;
//using Microsoft.KernelMemory.Models;
//using OpenAI;
//using OpenAI.Chat;

//namespace Microsoft.KernelMemory.AI.AzureOpenAI;

//[Experimental("KMEXP01")]
//public sealed class AzureOpenAITextGenerator : ITextGenerator
//{
//    private readonly ITextTokenizer _textTokenizer;
//    private readonly OpenAIClient _client;
//    private readonly ILogger<AzureOpenAITextGenerator> _log;
//    private readonly bool _useTextCompletionProtocol;
//    private readonly string _deployment;

//    public AzureOpenAITextGenerator(
//        AzureOpenAIConfig config,
//        ITextTokenizer? textTokenizer = null,
//        ILoggerFactory? loggerFactory = null,
//        HttpClient? httpClient = null)
//    {
//        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<AzureOpenAITextGenerator>();

//        if (textTokenizer == null)
//        {
//            this._log.LogWarning(
//                "Tokenizer not specified, will use {0}. The token count might be incorrect, causing unexpected errors",
//                nameof(GPT4Tokenizer));
//            textTokenizer = new GPT4Tokenizer();
//        }

//        this._textTokenizer = textTokenizer;

//        if (string.IsNullOrEmpty(config.Endpoint))
//        {
//            throw new ConfigurationException($"Azure OpenAI: {config.Endpoint} is empty");
//        }

//        if (string.IsNullOrEmpty(config.Deployment))
//        {
//            throw new ConfigurationException($"Azure OpenAI: {config.Deployment} is empty");
//        }

//        this._useTextCompletionProtocol = config.APIType == AzureOpenAIConfig.APITypes.TextCompletion;
//        this._deployment = config.Deployment;
//        this.MaxTokenTotal = config.MaxTokenTotal;

//        OpenAIClientOptions options = new()
//        {
//            Endpoint = new Uri(config.Endpoint),
//            RetryPolicy = new ClientRetryPolicy(maxRetries: Math.Max(0, config.MaxRetries)), //, new SequentialDelayStrategy()
//            //Diagnostics =
//            //{
//            //    IsTelemetryEnabled = Telemetry.IsTelemetryEnabled,
//            //    ApplicationId = Telemetry.HttpUserAgent,
//            //}
//        };

//        if (httpClient is not null)
//        {
//            options.Transport = new HttpClientPipelineTransport(httpClient);
//        }

//        switch (config.Auth)
//        {
//            //case AzureOpenAIConfig.AuthTypes.AzureIdentity:
//            //    this._client = new OpenAIClient(new DefaultAzureCredential(), options);
//            //    break;

//            //case AzureOpenAIConfig.AuthTypes.ManualTokenCredential:
//            //    this._client = new OpenAIClient(config.GetTokenCredential(), options);
//            //    break;

//            case AzureOpenAIConfig.AuthTypes.APIKey:
//                if (string.IsNullOrEmpty(config.APIKey))
//                {
//                    throw new ConfigurationException($"Azure OpenAI: {config.APIKey} is empty");
//                }

//                this._client = new OpenAIClient(new ApiKeyCredential(config.APIKey), options);
//                break;

//            default:
//                throw new ConfigurationException($"Azure OpenAI: authentication type '{config.Auth:G}' is not supported");
//        }
//    }

//    /// <inheritdoc/>
//    public int MaxTokenTotal { get; }

//    /// <inheritdoc/>
//    public int CountTokens(string text)
//    {
//        return this._textTokenizer.CountTokens(text);
//    }

//    /// <inheritdoc/>
//    public IReadOnlyList<string> GetTokens(string text)
//    {
//        return this._textTokenizer.GetTokens(text);
//    }

//    /// <inheritdoc/>
//    public async IAsyncEnumerable<string> GenerateTextAsync(
//        string prompt,
//        TextGenerationOptions options,
//        [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        if (this._useTextCompletionProtocol)
//        {
//            this._log.LogTrace("Sending text generation request, deployment '{0}'", this._deployment);

//            var openaiOptions = new ChatCompletionOptions
//            {
//                MaxTokens = options.MaxTokens,
//                Temperature = (float)options.Temperature,
//                //NucleusSamplingFactor = (float)options.NucleusSampling,
//                FrequencyPenalty = (float)options.FrequencyPenalty,
//                PresencePenalty = (float)options.PresencePenalty,
//                //ChoicesPerPrompt = 1,
//            };

//            if (options.StopSequences is { Count: > 0 })
//            {
//                foreach (var s in options.StopSequences) { openaiOptions.StopSequences.Add(s); }
//            }

//            //if (options.TokenSelectionBiases is { Count: > 0 })
//            //{
//            //    foreach (var (token, bias) in options.TokenSelectionBiases) { openaiOptions.TokenSelectionBiases.Add(token, (int)bias); }
//            //}

//            var messages = new List<ChatMessage>();
//            messages.Add(new SystemChatMessage(prompt));

//            IAsyncEnumerator<StreamingChatCompletionUpdate> response = this._client.GetChatClient(this._deployment).CompleteChatStreamingAsync(messages, openaiOptions).GetAsyncEnumerator(cancellationToken);
//            //await using (IAsyncEnumerator<StreamingChatCompletionUpdate> response = this._client.GetChatClient(this._deployment).CompleteChatStreamingAsync(messages, openaiOptions, cancellationToken).GetAsyncEnumerator(cancellationToken))
//            //{
//            while (await response.MoveNextAsync().ConfigureAwait(false))
//            {
//                foreach (var text in response.Current.ContentUpdate.AsEnumerable())
//                {
//                    yield return text.Text;
//                }
//            }
//            //}
//        }
//        else
//        {
//            this._log.LogTrace("Sending chat message generation request, deployment '{0}'", this._deployment);

//            var openaiOptions = new ChatCompletionOptions
//            {
//                //DeploymentName = this._deployment,
//                MaxTokens = options.MaxTokens,
//                Temperature = (float)options.Temperature,
//                //NucleusSamplingFactor = (float)options.NucleusSampling,
//                FrequencyPenalty = (float)options.FrequencyPenalty,
//                PresencePenalty = (float)options.PresencePenalty,
//                // ChoiceCount = 1,
//            };

//            if (options.StopSequences is { Count: > 0 })
//            {
//                foreach (var s in options.StopSequences) { openaiOptions.StopSequences.Add(s); }
//            }

//            //if (options.TokenSelectionBiases is { Count: > 0 })
//            //{
//            //    foreach (var (token, bias) in options.TokenSelectionBiases) { openaiOptions.TokenSelectionBiases.Add(token, (int)bias); }
//            //}

//            var messages = new List<ChatMessage>();
//            messages.Add(new SystemChatMessage(prompt));

//            IAsyncEnumerator<StreamingChatCompletionUpdate> response = this._client.GetChatClient(this._deployment).CompleteChatStreamingAsync(messages, openaiOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
//            //await using (IAsyncEnumerator<StreamingChatCompletionUpdate> response = this._client.GetChatClient(this._deployment).CompleteChatStreamingAsync(messages, openaiOptions, cancellationToken).GetAsyncEnumerator(cancellationToken))
//            //{
//            while (await response.MoveNextAsync().ConfigureAwait(false))
//            {
//                foreach (var text in response.Current.ContentUpdate.AsEnumerable())
//                {
//                    yield return text.Text;
//                }
//            }
//            //}
//        }
//    }

//    public async IAsyncEnumerable<TextGenerationResult> GenerateTextChunkAsync(
//        string prompt,
//        TextGenerationOptions options,
//        [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        if (this._useTextCompletionProtocol)
//        {
//            this._log.LogTrace("Sending text generation request, deployment '{0}'", this._deployment);

//            var openaiOptions = new ChatCompletionOptions
//            {
//                //DeploymentName = this._deployment,
//                MaxTokens = options.MaxTokens,
//                Temperature = (float)options.Temperature,
//                //NucleusSamplingFactor = (float)options.NucleusSampling,
//                FrequencyPenalty = (float)options.FrequencyPenalty,
//                PresencePenalty = (float)options.PresencePenalty,
//                //ChoicesPerPrompt = 1,
//            };

//            if (options.StopSequences is { Count: > 0 })
//            {
//                foreach (var s in options.StopSequences) { openaiOptions.StopSequences.Add(s); }
//            }

//            //if (options.TokenSelectionBiases is { Count: > 0 })
//            //{
//            //    foreach (var (token, bias) in options.TokenSelectionBiases) { openaiOptions.TokenSelectionBiases.Add(token, (int)bias); }
//            //}

//            var messages = new List<ChatMessage>();
//            messages.Add(new SystemChatMessage(prompt));

//            IAsyncEnumerator<StreamingChatCompletionUpdate> response = this._client.GetChatClient(this._deployment).CompleteChatStreamingAsync(messages, openaiOptions).GetAsyncEnumerator(cancellationToken);
//            //await using (IAsyncEnumerator<StreamingChatCompletionUpdate> response = this._client.GetChatClient(this._deployment).CompleteChatStreamingAsync(messages, openaiOptions, cancellationToken).GetAsyncEnumerator(cancellationToken))
//            //{
//                StreamingChatCompletionUpdate? currentUpdate;
//            while (await response.MoveNextAsync().ConfigureAwait(false))
//            {
//                currentUpdate = response.Current;
//                foreach (var text in currentUpdate.ContentUpdate.AsEnumerable())
//                {
//                    yield return new TextGenerationResult(text.Text, currentUpdate.Usage.OutputTokens, currentUpdate.Usage.InputTokens, currentUpdate.Usage.TotalTokens);
//                }
//            }
//            //}
//        }
//        else
//        {
//            this._log.LogTrace("Sending chat message generation request, deployment '{0}'", this._deployment);

//            var openaiOptions = new ChatCompletionOptions
//            {
//                //DeploymentName = this._deployment,
//                MaxTokens = options.MaxTokens,
//                Temperature = (float)options.Temperature,
//                //NucleusSamplingFactor = (float)options.NucleusSampling,
//                FrequencyPenalty = (float)options.FrequencyPenalty,
//                PresencePenalty = (float)options.PresencePenalty,
//                //ChoicesPerPrompt = 1,
//            };

//            if (options.StopSequences is { Count: > 0 })
//            {
//                foreach (var s in options.StopSequences) { openaiOptions.StopSequences.Add(s); }
//            }

//            //if (options.TokenSelectionBiases is { Count: > 0 })
//            //{
//            //    foreach (var (token, bias) in options.TokenSelectionBiases) { openaiOptions.TokenSelectionBiases.Add(token, (int)bias); }
//            //}

//            var messages = new List<ChatMessage>();
//            messages.Add(new SystemChatMessage(prompt));

//            IAsyncEnumerator<StreamingChatCompletionUpdate> response = this._client.GetChatClient(this._deployment).CompleteChatStreamingAsync(messages, openaiOptions).GetAsyncEnumerator(cancellationToken);
//            //await using (IAsyncEnumerator<StreamingChatCompletionUpdate> response = this._client.GetChatClient(this._deployment).CompleteChatStreamingAsync(messages, openaiOptions, cancellationToken).GetAsyncEnumerator(cancellationToken))
//            //{
//                StreamingChatCompletionUpdate? currentUpdate;
//            while (await response.MoveNextAsync().ConfigureAwait(false))
//            {
//                currentUpdate = response.Current;
//                foreach (var text in currentUpdate.ContentUpdate.AsEnumerable())
//                {
//                    yield return new TextGenerationResult(text.Text, currentUpdate.Usage.OutputTokens, currentUpdate.Usage.InputTokens, currentUpdate.Usage.TotalTokens);
//                }
//            }
//            //}
//        }
//    }
//}
