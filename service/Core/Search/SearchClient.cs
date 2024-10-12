// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Context;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Prompts;
using Microsoft.KernelMemory.Models;
using Microsoft.KernelMemory.Enums;

namespace Microsoft.KernelMemory.Search;

public sealed class SearchClient : ISearchClient
{
    private readonly IMemoryDb _memoryDb;
    private readonly ITextGenerator _textGenerator;
    private readonly SearchClientConfig _config;
    private readonly ILogger<SearchClient> _log;
    private readonly string _answerPrompt;
    private readonly string _rephraseQuestionPrompt;

    public SearchClient(
        IMemoryDb memoryDb,
        ITextGenerator textGenerator,
        SearchClientConfig? config = null,
        IPromptProvider? promptProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        this._memoryDb = memoryDb;
        this._textGenerator = textGenerator;
        this._config = config ?? new SearchClientConfig();
        this._config.Validate();

        promptProvider ??= new EmbeddedPromptProvider();
        this._answerPrompt = promptProvider.ReadPrompt(Constants.PromptNamesAnswerWithFacts);
        this._rephraseQuestionPrompt = promptProvider.ReadPrompt(Constants.PromptNamesRephraseQuestion);

        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<SearchClient>();

        if (this._memoryDb == null)
        {
            throw new KernelMemoryException("Search memory DB not configured");
        }

        if (this._textGenerator == null)
        {
            throw new KernelMemoryException("Text generator not configured");
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<string>> ListIndexesAsync(CancellationToken cancellationToken = default)
    {
        return this._memoryDb.GetIndexesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SearchResult> SearchAsync(
        string index,
        string query,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        int limit = -1,
        IContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0) { limit = this._config.MaxMatchesCount; }

        var result = new SearchResult
        {
            Query = query,
            Results = new List<Citation>()
        };

        if (string.IsNullOrWhiteSpace(query) && (filters == null || filters.Count == 0))
        {
            this._log.LogWarning("No query or filters provided");
            return result;
        }

        var list = new List<(MemoryRecord memory, double relevance)>();
        if (!string.IsNullOrEmpty(query))
        {
            this._log.LogTrace("Fetching relevant memories by similarity, min relevance {0}", minRelevance);
            IAsyncEnumerable<(MemoryRecord, double)> matches = this._memoryDb.GetSimilarListAsync(
                index: index,
                text: query,
                filters: filters,
                minRelevance: minRelevance,
                limit: limit,
                withEmbeddings: false,
                cancellationToken: cancellationToken);

            // Memories are sorted by relevance, starting from the most relevant
            await foreach ((MemoryRecord memory, double relevance) in matches.ConfigureAwait(false))
            {
                list.Add((memory, relevance));
            }
        }
        else
        {
            this._log.LogTrace("Fetching relevant memories by filtering");
            IAsyncEnumerable<MemoryRecord> matches = this._memoryDb.GetListAsync(
                index: index,
                filters: filters,
                limit: limit,
                withEmbeddings: false,
                cancellationToken: cancellationToken);

            await foreach (MemoryRecord memory in matches.ConfigureAwait(false))
            {
                list.Add((memory, float.MinValue));
            }
        }

        // Memories are sorted by relevance, starting from the most relevant
        foreach ((MemoryRecord memory, double relevance) in list)
        {
            // Note: a document can be composed by multiple files
            string documentId = memory.GetDocumentId(this._log);

            // Identify the file in case there are multiple files
            string fileId = memory.GetFileId(this._log);

            // Note: this is not a URL and perhaps could be dropped. For now it acts as a unique identifier. See also SourceUrl.
            string linkToFile = $"{index}/{documentId}/{fileId}";

            var partitionText = memory.GetPartitionText(this._log).Trim();
            if (string.IsNullOrEmpty(partitionText))
            {
                this._log.LogError("The document partition is empty, doc: {0}", memory.Id);
                continue;
            }

            // Relevance is `float.MinValue` when search uses only filters and no embeddings (see code above)
            if (relevance > float.MinValue) { this._log.LogTrace("Adding result with relevance {0}", relevance); }

            // If the file is already in the list of citations, only add the partition
            var citation = result.Results.FirstOrDefault(x => x.Link == linkToFile);
            if (citation == null)
            {
                citation = new Citation();
                result.Results.Add(citation);
            }

            // Add the partition to the list of citations
            citation.Index = index;
            citation.DocumentId = documentId;
            citation.FileId = fileId;
            citation.Link = linkToFile;
            citation.SourceContentType = memory.GetFileContentType(this._log);
            citation.SourceName = memory.GetFileName(this._log);
            citation.SourceUrl = memory.GetWebPageUrl(index);

            citation.Partitions.Add(new Citation.Partition
            {
                Text = partitionText,
                Relevance = (float)relevance,
                PartitionNumber = memory.GetPartitionNumber(this._log),
                SectionNumber = memory.GetSectionNumber(),
                LastUpdate = memory.GetLastUpdate(),
                Tags = memory.Tags,
            });

            // In cases where a buggy storage connector is returning too many records
            if (result.Results.Count >= this._config.MaxMatchesCount)
            {
                break;
            }
        }

        if (result.Results.Count == 0)
        {
            this._log.LogDebug("No memories found");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<MemoryAnswer> AskAsync(
        string index,
        string question,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        IContext? context = null,
        CancellationToken cancellationToken = default)
    {
        string emptyAnswer = context.GetCustomEmptyAnswerTextOrDefault(this._config.EmptyAnswer);
        string answerPrompt = context.GetCustomRagPromptOrDefault(this._answerPrompt);
        string factTemplate = context.GetCustomRagFactTemplateOrDefault(this._config.FactTemplate);
        if (!factTemplate.EndsWith('\n')) { factTemplate += "\n"; }

        var noAnswerFound = new MemoryAnswer
        {
            Question = question,
            NoResult = true,
            Result = emptyAnswer,
        };

        if (string.IsNullOrEmpty(question))
        {
            this._log.LogWarning("No question provided");
            noAnswerFound.NoResultReason = "No question provided";
            return noAnswerFound;
        }

        var facts = new StringBuilder();
        var maxTokens = this._config.MaxAskPromptSize > 0
            ? this._config.MaxAskPromptSize
            : this._textGenerator.MaxTokenTotal;
        var tokensAvailable = maxTokens
                              - this._textGenerator.CountTokens(answerPrompt)
                              - this._textGenerator.CountTokens(question)
                              - this._config.AnswerTokens;

        var factsUsedCount = 0;
        var factsAvailableCount = 0;
        var answer = noAnswerFound;

        // Rephrasing the question for better answers
        var rephrasedQuestion = new StringBuilder();
        await foreach (var x in this.GenerateRephrasedQuestion(question, context, cancellationToken).ConfigureAwait(false))
        {
            rephrasedQuestion.Append(x);
        }
        if (rephrasedQuestion.Length > 0)
        {
            question = rephrasedQuestion.ToString();
        }

        this._log.LogTrace("Fetching relevant memories");
        IAsyncEnumerable<(MemoryRecord, double)> matches = this._memoryDb.GetSimilarListAsync(
            index: index,
            text: question,
            filters: filters,
            minRelevance: minRelevance,
            limit: this._config.MaxMatchesCount,
            withEmbeddings: false,
            cancellationToken: cancellationToken);

        // Memories are sorted by relevance, starting from the most relevant
        await foreach ((MemoryRecord memory, double relevance) in matches.ConfigureAwait(false))
        {
            // Note: a document can be composed by multiple files
            string documentId = memory.GetDocumentId(this._log);

            // Identify the file in case there are multiple files
            string fileId = memory.GetFileId(this._log);

            // Note: this is not a URL and perhaps could be dropped. For now it acts as a unique identifier. See also SourceUrl.
            string linkToFile = $"{index}/{documentId}/{fileId}";

            string fileName = memory.GetFileName(this._log);

            string webPageUrl = memory.GetWebPageUrl(index);

            var partitionText = memory.GetPartitionText(this._log).Trim();
            if (string.IsNullOrEmpty(partitionText))
            {
                this._log.LogError("The document partition is empty, doc: {0}", memory.Id);
                continue;
            }

            factsAvailableCount++;

            var fact = PromptUtils.RenderFactTemplate(
                template: factTemplate,
                factContent: partitionText,
                source: (fileName == "content.url" ? webPageUrl : fileName),
                relevance: relevance.ToString("P1", CultureInfo.CurrentCulture),
                recordId: memory.Id,
                tags: memory.Tags,
                metadata: memory.Payload);

            // Use the partition/chunk only if there's room for it
            var size = this._textGenerator.CountTokens(fact);
            if (size >= tokensAvailable)
            {
                // Stop after reaching the max number of tokens
                break;
            }

            factsUsedCount++;
            this._log.LogTrace("Adding text {0} with relevance {1}", factsUsedCount, relevance);

            facts.Append(fact);
            tokensAvailable -= size;

            // If the file is already in the list of citations, only add the partition
            var citation = answer.RelevantSources.FirstOrDefault(x => x.Link == linkToFile);
            if (citation == null)
            {
                citation = new Citation();
                answer.RelevantSources.Add(citation);
            }

            // Add the partition to the list of citations
            citation.Index = index;
            citation.DocumentId = documentId;
            citation.FileId = fileId;
            citation.Link = linkToFile;
            citation.SourceContentType = memory.GetFileContentType(this._log);
            citation.SourceName = fileName;
            citation.SourceUrl = memory.GetWebPageUrl(index);

            citation.Partitions.Add(new Citation.Partition
            {
                Text = partitionText,
                Relevance = (float)relevance,
                PartitionNumber = memory.GetPartitionNumber(this._log),
                SectionNumber = memory.GetSectionNumber(),
                LastUpdate = memory.GetLastUpdate(),
                Tags = memory.Tags,
            });

            // In cases where a buggy storage connector is returning too many records
            if (factsUsedCount >= this._config.MaxMatchesCount)
            {
                break;
            }
        }

        if (factsAvailableCount > 0 && factsUsedCount == 0)
        {
            this._log.LogError("Unable to inject memories in the prompt, not enough tokens available");
            noAnswerFound.NoResultReason = "Unable to use memories";
            return noAnswerFound;
        }

        if (factsUsedCount == 0)
        {
            this._log.LogWarning("No memories available");
            noAnswerFound.NoResultReason = "No memories available";
            return noAnswerFound;
        }

        var charsGenerated = 0;
        var prompt = string.Empty;
        var completeAnswer = new StringBuilder();
        var watch = new Stopwatch();
        watch.Restart();
        await foreach (var x in this.GenerateAnswer(question, facts.ToString(), context, cancellationToken, out prompt).ConfigureAwait(false))
        {
            completeAnswer.Append(x);
            if (this._log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace) && completeAnswer.Length - charsGenerated >= 30)
            {
                charsGenerated = completeAnswer.Length;
                this._log.LogTrace("{0} chars generated", charsGenerated);
            }
        }

        watch.Stop();

        answer.Result = completeAnswer.ToString();
        answer.NoResult = ValueIsEquivalentTo(answer.Result, emptyAnswer);
        if (answer.NoResult)
        {
            answer.NoResultReason = "No relevant memories found";
            this._log.LogTrace("Answer generated in {0} msecs. No relevant memories found", watch.ElapsedMilliseconds);
        }
        else
        {
            this._log.LogTrace("Answer generated in {0} msecs", watch.ElapsedMilliseconds);
        }

        answer.Prompt = prompt;

        // Count Prompt Tokens
        var promptTokens = this._textGenerator.CountTokens(prompt);
        var completionTokens = this._textGenerator.CountTokens(completeAnswer.ToString());
        answer.CompletionUsage = new CompletionUsage(completionTokens: completionTokens, promptTokens: promptTokens, totalTokens: promptTokens + completionTokens);
        if (this._log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            this._log.LogDebug("Running RAG prompt, size: {0} tokens, requesting max {1} tokens",
                promptTokens,
                this._config.AnswerTokens);
        }

        return answer;
    }

    public async IAsyncEnumerable<MemoryAnswer> AskAsyncChunk(
        string index,
        string question,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        IContext? context = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string emptyAnswer = context.GetCustomEmptyAnswerTextOrDefault(this._config.EmptyAnswer);
        string eosToken = context.GetCustomEosTokenOrDefault("end");
        string answerPrompt = context.GetCustomRagPromptOrDefault(this._answerPrompt);
        string factTemplate = context.GetCustomRagFactTemplateOrDefault(this._config.FactTemplate);
        if (!factTemplate.EndsWith('\n')) { factTemplate += "\n"; }

        var noAnswerFound = new MemoryAnswer
        {
            Question = question,
            NoResult = true,
            Result = emptyAnswer
        };

        if (string.IsNullOrEmpty(question))
        {
            this._log.LogWarning("No question provided");
            noAnswerFound.NoResultReason = "No question provided";
            yield return noAnswerFound;
            yield break;
        }

        var facts = new StringBuilder();
        var maxTokens = this._config.MaxAskPromptSize > 0
            ? this._config.MaxAskPromptSize
            : this._textGenerator.MaxTokenTotal;
        var tokensAvailable = maxTokens
                              - this._textGenerator.CountTokens(answerPrompt)
                              - this._textGenerator.CountTokens(question)
                              - this._config.AnswerTokens;

        var factsUsedCount = 0;
        var factsAvailableCount = 0;
        var answer = noAnswerFound;

        // Rephrasing the question for better answers
        var rephrasedQuestion = new StringBuilder();
        await foreach (var x in this.GenerateRephrasedQuestion(question, context, cancellationToken).ConfigureAwait(false))
        {
            rephrasedQuestion.Append(x);
        }
        if (rephrasedQuestion.Length > 0)
        {
            question = rephrasedQuestion.ToString();
        }

        this._log.LogTrace("Fetching relevant memories");
        IAsyncEnumerable<(MemoryRecord, double)> matches = this._memoryDb.GetSimilarListAsync(
            index: index,
            text: question,
            filters: filters,
            minRelevance: minRelevance,
            limit: this._config.MaxMatchesCount,
            withEmbeddings: false,
            cancellationToken: cancellationToken);

        // Memories are sorted by relevance, starting from the most relevant
        await foreach ((MemoryRecord memory, double relevance) in matches.ConfigureAwait(false))
        {
            // Note: a document can be composed by multiple files
            string documentId = memory.GetDocumentId(this._log);

            // Identify the file in case there are multiple files
            string fileId = memory.GetFileId(this._log);

            // Note: this is not a URL and perhaps could be dropped. For now it acts as a unique identifier. See also SourceUrl.
            string linkToFile = $"{index}/{documentId}/{fileId}";

            string fileName = memory.GetFileName(this._log);

            string webPageUrl = memory.GetWebPageUrl(index);

            var partitionText = memory.GetPartitionText(this._log).Trim();
            if (string.IsNullOrEmpty(partitionText))
            {
                this._log.LogError("The document partition is empty, doc: {0}", memory.Id);
                continue;
            }

            factsAvailableCount++;

            var fact = PromptUtils.RenderFactTemplate(
                template: factTemplate,
                factContent: partitionText,
                source: (fileName == "content.url" ? webPageUrl : fileName),
                relevance: relevance.ToString("P1", CultureInfo.CurrentCulture),
                recordId: memory.Id,
                tags: memory.Tags,
                metadata: memory.Payload);

            // Use the partition/chunk only if there's room for it
            var size = this._textGenerator.CountTokens(fact);
            if (size >= tokensAvailable)
            {
                // Stop after reaching the max number of tokens
                break;
            }

            factsUsedCount++;
            this._log.LogTrace("Adding text {0} with relevance {1}", factsUsedCount, relevance);

            facts.Append(fact);
            tokensAvailable -= size;

            // If the file is already in the list of citations, only add the partition
            var citation = answer.RelevantSources.FirstOrDefault(x => x.Link == linkToFile);
            if (citation == null)
            {
                citation = new Citation();
                answer.RelevantSources.Add(citation);
            }

            // Add the partition to the list of citations
            citation.Index = index;
            citation.DocumentId = documentId;
            citation.FileId = fileId;
            citation.Link = linkToFile;
            citation.SourceContentType = memory.GetFileContentType(this._log);
            citation.SourceName = fileName;
            citation.SourceUrl = memory.GetWebPageUrl(index);

            citation.Partitions.Add(new Citation.Partition
            {
                Text = partitionText,
                Relevance = (float)relevance,
                PartitionNumber = memory.GetPartitionNumber(this._log),
                SectionNumber = memory.GetSectionNumber(),
                LastUpdate = memory.GetLastUpdate(),
                Tags = memory.Tags,
            });

            // In cases where a buggy storage connector is returning too many records
            if (factsUsedCount >= this._config.MaxMatchesCount)
            {
                break;
            }
        }

        if (factsAvailableCount > 0 && factsUsedCount == 0)
        {
            this._log.LogError("Unable to inject memories in the prompt, not enough tokens available");
            noAnswerFound.NoResultReason = "Unable to use memories";
            yield return noAnswerFound;
            yield break;
        }

        if (factsUsedCount == 0)
        {
            this._log.LogWarning("No memories available");
            noAnswerFound.NoResultReason = "No memories available";
            yield return noAnswerFound;
            yield break;
        }
        var charsGenerated = 0;
        var prompt = string.Empty;
        var completeAnswer = new StringBuilder();
        await foreach (var x in this.GenerateAnswerChunk(question, facts.ToString(), context, cancellationToken, out prompt).ConfigureAwait(true))
        {
            completeAnswer.Append(x.GeneratedText);
            var text = new StringBuilder().Append(x.GeneratedText);
            if (this._log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace) && text.Length - charsGenerated >= 30)
            {
                charsGenerated = text.Length;
                this._log.LogTrace("{0} chars generated", charsGenerated);
            }
            var newAnswer = new MemoryAnswer
            {
                Question = question,
                NoResult = false,
                Result = text.ToString(),
                CompletionUsage = new CompletionUsage(x.CompletionTokens, x.PromptTokens, x.TotalTokens)
            };
            this._log.LogInformation("Chunk: '{0}", newAnswer.Result);
            yield return newAnswer;
        }
        answer.Result = eosToken;
        answer.Prompt = prompt;

        // Count Prompt Tokens
        var promptTokens = this._textGenerator.CountTokens(prompt);
        var completionTokens = this._textGenerator.CountTokens(completeAnswer.ToString());
        answer.CompletionUsage = new CompletionUsage(completionTokens: completionTokens, promptTokens: promptTokens, totalTokens: promptTokens + completionTokens);
        if (this._log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            this._log.LogDebug("Running RAG prompt, size: {0} tokens, requesting max {1} tokens",
                promptTokens,
                this._config.AnswerTokens);
        }

        this._log.LogInformation("Eos token: '{0}", answer.Result);
        yield return answer;
    }

    private IAsyncEnumerable<string> GenerateRephrasedQuestion(string question, IContext? context, CancellationToken token)
    {
        // LLM Options
        int maxTokens = context.GetCustomRagMaxTokensOrDefault(this._config.AnswerTokens);
        double temperature = context.GetCustomRagTemperatureOrDefault(this._config.Temperature);
        double nucleusSampling = context.GetCustomRagNucleusSamplingOrDefault(this._config.TopP);
        var options = new TextGenerationOptions
        {
            MaxTokens = maxTokens,
            Temperature = temperature,
            NucleusSampling = nucleusSampling,
            PresencePenalty = this._config.PresencePenalty,
            FrequencyPenalty = this._config.FrequencyPenalty,
            StopSequences = this._config.StopSequences,
            TokenSelectionBiases = this._config.TokenSelectionBiases,
        };

        // Generate Prompt
        List<PromptSegment> promptSegments = this.GenerateRephrasedQuestionPromptSegments(question, context);

        // Generate Answer
        return this._textGenerator.CompleteChatAsync(promptSegments, options, token);
    }

    private List<PromptSegment> GenerateRephrasedQuestionPromptSegments(string question, IContext? context)
    {
        List<PromptSegment> promptSegments = new();
        var systemPrompt = new StringBuilder();

        // System Prompt for Question Rephrasing
        systemPrompt.Append(context.GetRephrasedQuestionRagPromptOrDefault(this._rephraseQuestionPrompt));

        // Additional Prompt
        var additionalPrompt = context.GetCustomRagAdditionalPromptOrDefault(string.Empty);
        if (!string.IsNullOrEmpty(additionalPrompt))
        {
            additionalPrompt = $"\r\n\r\nAdditional Instructions:\r\n{additionalPrompt}";
            systemPrompt.Append(additionalPrompt);
        }

        // Chat History
        var previousQuestions = new StringBuilder();
        try
        {
            var chatHistory = context.GetCustomRagChatHistoryOrDefault(null);
            if (chatHistory != null)
            {
                string[] chatSegments;
                foreach (var chat in chatHistory)
                {
                    chatSegments = chat.Split("__");
                    switch (chatSegments[0].ToUpper(CultureInfo.CurrentCulture))
                    {
                        case "USER":
                            previousQuestions.Append("  user: " + chatSegments[1]);
                            break;
                        case "ASSISTANT":
                            previousQuestions.Append("  asssistant: " + chatSegments[1]);
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this._log.LogWarning("Could not parse the chat history.", ex);
        }
        if (previousQuestions.Length > 0)
        {
            var previousQuestion = $"\r\n\r\n====\r\n\r\nChat History:\r\n{previousQuestions}";
            systemPrompt.Append(previousQuestion);
        }

        // User Current Question
        systemPrompt.Append("\r\nNext Question: " + question.Trim());

        promptSegments.Add(new PromptSegment(ChatRoles.System, "\r\n" + systemPrompt));

        return promptSegments;
    }

    private IAsyncEnumerable<string> GenerateAnswer(string question, string facts, IContext? context, CancellationToken token, out string prompt)
    {
        // LLM Options
        int maxTokens = context.GetCustomRagMaxTokensOrDefault(this._config.AnswerTokens);
        double temperature = context.GetCustomRagTemperatureOrDefault(this._config.Temperature);
        double nucleusSampling = context.GetCustomRagNucleusSamplingOrDefault(this._config.TopP);
        var options = new TextGenerationOptions
        {
            MaxTokens = maxTokens,
            Temperature = temperature,
            NucleusSampling = nucleusSampling,
            PresencePenalty = this._config.PresencePenalty,
            FrequencyPenalty = this._config.FrequencyPenalty,
            StopSequences = this._config.StopSequences,
            TokenSelectionBiases = this._config.TokenSelectionBiases,
        };

        // Generate Prompt
        List<PromptSegment> promptSegments = this.GenerateAnswerPromptSegments(question, facts, context);
        prompt = GenerateAnswerPrompt(promptSegments);

        // Generate Answer
        return this._textGenerator.CompleteChatAsync(promptSegments, options, token);
    }

    private List<PromptSegment> GenerateAnswerPromptSegments(string question, string facts, IContext? context)
    {
        List<PromptSegment> promptSegments = new();
        var systemPrompt = new StringBuilder();

        // System Prompt
        string emptyAnswer = context.GetCustomEmptyAnswerTextOrDefault(this._config.EmptyAnswer);
        var prompt = context.GetCustomRagPromptOrDefault(this._answerPrompt);
        prompt = prompt.Replace("{{$notFound}}", emptyAnswer, StringComparison.OrdinalIgnoreCase);
        systemPrompt.Append(prompt);

        // Additional Prompt
        var additionalPrompt = context.GetCustomRagAdditionalPromptOrDefault(string.Empty);
        if (!string.IsNullOrEmpty(additionalPrompt))
        {
            additionalPrompt = $"\r\n\r\nAdditional Instructions:\r\n{additionalPrompt}";
            systemPrompt.Append(additionalPrompt);
        }

        // Facts
        if (!string.IsNullOrEmpty(facts.Trim()))
        {
            systemPrompt.Append("\r\n\r\nFacts:\r\n" + facts.Trim());
        }

        promptSegments.Add(new PromptSegment(ChatRoles.System, "\r\n" + systemPrompt));

        // Chat History
        try
        {
            var chatHistory = context.GetCustomRagChatHistoryOrDefault(null);
            if (chatHistory != null)
            {
                string[] chatSegments;
                foreach (var chat in chatHistory)
                {
                    chatSegments = chat.Split("__");
                    switch (chatSegments[0].ToUpper(CultureInfo.CurrentCulture))
                    {
                        case "USER":
                            promptSegments.Add(new PromptSegment(ChatRoles.User, chatSegments[1]));
                            break;
                        case "ASSISTANT":
                            promptSegments.Add(new PromptSegment(ChatRoles.Assistant, chatSegments[1]));
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this._log.LogWarning("Could not parse the chat history.", ex);
        }

        // User Question
        promptSegments.Add(new PromptSegment(ChatRoles.User, question.Trim()));

        return promptSegments;
    }

    private static string GenerateAnswerPrompt(List<PromptSegment> promptSegments)
    {
        var promptBuilder = new StringBuilder();
        promptSegments.ForEach((f) =>
        {
            promptBuilder.AppendLine(f.ChatRole.ToString().ToLower(CultureInfo.CurrentCulture) + ": " + f.Message);
        });

        return promptBuilder.ToString();
    }

    private IAsyncEnumerable<TextGenerationResult> GenerateAnswerChunk(string question, string facts, IContext? context, CancellationToken token, out string prompt)
    {
        // LLM Options
        int maxTokens = context.GetCustomRagMaxTokensOrDefault(this._config.AnswerTokens);
        double temperature = context.GetCustomRagTemperatureOrDefault(this._config.Temperature);
        double nucleusSampling = context.GetCustomRagNucleusSamplingOrDefault(this._config.TopP);
        var options = new TextGenerationOptions
        {
            MaxTokens = maxTokens,
            Temperature = temperature,
            NucleusSampling = nucleusSampling,
            PresencePenalty = this._config.PresencePenalty,
            FrequencyPenalty = this._config.FrequencyPenalty,
            StopSequences = this._config.StopSequences,
            TokenSelectionBiases = this._config.TokenSelectionBiases,
        };

        // Generate Prompt
        List<PromptSegment> promptSegments = this.GenerateAnswerPromptSegments(question, facts, context);
        prompt = GenerateAnswerPrompt(promptSegments);

        // Generate Answer
        return this._textGenerator.CompleteChatChunkAsync(promptSegments, options, token);
    }

    private static bool ValueIsEquivalentTo(string value, string target)
    {
        value = value.Trim().Trim('.', '"', '\'', '`', '~', '!', '?', '@', '#', '$', '%', '^', '+', '*', '_', '-', '=', '|', '\\', '/', '(', ')', '[', ']', '{', '}', '<', '>');
        target = target.Trim().Trim('.', '"', '\'', '`', '~', '!', '?', '@', '#', '$', '%', '^', '+', '*', '_', '-', '=', '|', '\\', '/', '(', ')', '[', ']', '{', '}', '<', '>');
        return string.Equals(value, target, StringComparison.OrdinalIgnoreCase);
    }
}
