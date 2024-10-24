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
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Text.Json;
using DocumentFormat.OpenXml.Drawing;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

namespace Microsoft.KernelMemory.Search;

public sealed class SearchClient : ISearchClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryDb _memoryDb;
    private readonly ITextGenerator _textGenerator;
    private readonly SearchClientConfig _config;
    private readonly ILogger<SearchClient> _log;
    private readonly string _answerPrompt;
    private readonly string _rephraseQuestionPrompt;

    // Regex pattern to match the required format
    private readonly string _factSyntaxPattern = @"==== \[File:(.*?)\]";

    public SearchClient(
        IMemoryDb memoryDb,
        ITextGenerator textGenerator,
        IHttpClientFactory httpClientFactory,
        SearchClientConfig? config = null,
        IPromptProvider? promptProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        this._memoryDb = memoryDb;
        this._textGenerator = textGenerator;
        this._config = config ?? new SearchClientConfig();
        this._config.Validate();

        this._httpClientFactory = httpClientFactory;

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
        // string answerPrompt = context.GetCustomRagPromptOrDefault(this._answerPrompt);
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

        // var facts = new StringBuilder();
        List<string> facts = new();
        List<string> factCitations = new();
        //var maxTokens = this._config.MaxAskPromptSize > 0
        //    ? this._config.MaxAskPromptSize
        //    : this._textGenerator.MaxTokenTotal;
        //var tokensAvailable = maxTokens
        //                      - this._textGenerator.CountTokens(answerPrompt)
        //                      - this._textGenerator.CountTokens(question)
        //                      - this._config.AnswerTokens;

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

            int partitionNumber = memory.GetPartitionNumber(this._log);
            int sectionNumber = memory.GetSectionNumber();

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
                fileId: fileId,
                documentId: documentId,
                partitionNumber: partitionNumber,
                sectionNumber: sectionNumber,
                recordId: memory.Id,
                tags: memory.Tags,
                metadata: memory.Payload);

            //// Use the partition/chunk only if there's room for it
            //var size = this._textGenerator.CountTokens(fact);
            //if (size >= tokensAvailable)
            //{
            //    // Stop after reaching the max number of tokens
            //    break;
            //}

            factsUsedCount++;
            this._log.LogTrace("Adding text {0} with relevance {1}", factsUsedCount, relevance);

            var factCitation = this.GetFactCitation(fact);
            if (!string.IsNullOrEmpty(factCitation))
            {
                factCitations.Add(factCitation);
            }
            facts.Add(fact);
            //tokensAvailable -= size;

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
                PartitionNumber = partitionNumber,
                SectionNumber = sectionNumber,
                LastUpdate = memory.GetLastUpdate(),
                Tags = memory.Tags,
            });

            // In cases where a buggy storage connector is returning too many records
            if (factsUsedCount >= this._config.MaxMatchesCount)
            {
                break;
            }
        }

        //if (factsAvailableCount > 0 && factsUsedCount == 0)
        //{
        //    this._log.LogError("Unable to inject memories in the prompt, not enough tokens available");
        //    noAnswerFound.NoResultReason = "Unable to use memories";
        //    return noAnswerFound;
        //}

        //if (factsUsedCount == 0)
        //{
        //    this._log.LogWarning("No memories available");
        //    noAnswerFound.NoResultReason = "No memories available";
        //    return noAnswerFound;
        //}

        var charsGenerated = 0;
        var prompt = string.Empty;
        var completeAnswer = new StringBuilder();
        var watch = new Stopwatch();
        watch.Restart();
        await foreach (var chunk in this.GenerateAnswer(question, facts, factCitations, context, cancellationToken, out prompt).ConfigureAwait(false))
        {
            completeAnswer.Append(chunk);
            if (this._log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace) && completeAnswer.Length - charsGenerated >= 30)
            {
                charsGenerated = completeAnswer.Length;
                this._log.LogTrace("{0} chars generated", charsGenerated);
            }
        }
        watch.Stop();

        // Sanitize result
        var sanitizedAnswer = this.SanitizeAnswer(index, completeAnswer.ToString());
        answer.Result = sanitizedAnswer.Item1;

        // Add clean citations
        answer.RelevantSources.Clear(); //Remove the Azure Search memory based citations and add OpenAI based citations
        answer.RelevantSources = sanitizedAnswer.Item2;

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

    private string? GetFactCitation(string fact)
    {
        // Find all matches in the input string
        MatchCollection matches = Regex.Matches(fact, this._factSyntaxPattern);

        return matches.FirstOrDefault()?.Value ?? null;
    }

    private (string, List<Citation>) SanitizeAnswer(string index, string answer)
    {
        // Regex pattern to match the required format
        string pattern = @"\[File:(.*?)\]";

        // Find all matches in the input string
        MatchCollection matches = Regex.Matches(answer, pattern);

        var citations = new List<Citation>();
        string fileName = string.Empty;
        string fileId = string.Empty;
        string documentId = string.Empty;
        int partitionNumber;
        int sectionNumber;
        float relevance;

        // Loop through each match and print the content
        foreach (Match match in matches)
        {
            fileName = string.Empty;
            fileId = string.Empty;
            documentId = string.Empty;
            partitionNumber = 0;
            sectionNumber = 0;
            relevance = 0;

            if (match.Success)
            {
                var segments = match.Value.Substring(1, match.Value.Length - 2).Split(";");

                // Step 3: Loop through each segment and further split by ':'
                foreach (string segment in segments)
                {
                    // Check if the segment contains a colon to avoid index errors
                    if (segment.Contains(":", StringComparison.CurrentCulture))
                    {
                        string[] parts = segment.Split(':');

                        // Ensure we have exactly two parts before accessing
                        if (parts.Length == 2)
                        {
                            if (parts[0].Trim() == "File")
                            {
                                fileName = parts[1].Trim();
                            }
                            if (parts[0].Trim() == "FileId")
                            {
                                fileId = parts[1].Trim();
                            }
                            if (parts[0].Trim() == "DocId")
                            {
                                documentId = parts[1].Trim();
                            }
                            if (parts[0].Trim() == "PartNum")
                            {
                                int.TryParse(parts[1].Trim(), CultureInfo.CurrentCulture, out partitionNumber);
                            }
                            if (parts[0].Trim() == "SecNum")
                            {
                                int.TryParse(parts[1].Trim(), CultureInfo.CurrentCulture, out sectionNumber);
                            }
                            if (parts[0].Trim() == "Rel")
                            {
                                float.TryParse(parts[1].Replace("%", string.Empty, StringComparison.CurrentCulture).Trim(), CultureInfo.CurrentCulture, out relevance);
                            }
                        }
                    }
                }

                var citation = citations.FirstOrDefault((s) => s.FileId == fileId && s.DocumentId == documentId);
                if (citation == null)
                {
                    citation = new Citation()
                    {
                        Index = index,
                        DocumentId = documentId,
                        FileId = fileId,
                        Link = $"{index}/{documentId}/{fileId}",
                        SourceContentType = string.Empty,
                        SourceName = fileName,
                        SourceUrl = this.GetSourceUrl(index, documentId, fileName)
                    };
                    citations.Add(citation);
                }

                var citationPartition = citation.Partitions.FirstOrDefault((s) => s.PartitionNumber == partitionNumber && s.SectionNumber == sectionNumber);
                if (citationPartition == null)
                {
                    citationPartition = new Citation.Partition()
                    {
                        Relevance = (float)relevance,
                        PartitionNumber = partitionNumber,
                        SectionNumber = sectionNumber,
                        LastUpdate = DateTime.Now
                    };
                    citation.Partitions.Add(citationPartition);
                }
            }

            answer = answer.Replace(match.Value, string.Empty, StringComparison.CurrentCulture);
        }

        return (answer, citations);
    }

    private string GetSourceUrl(string index, string documentId, string fileName)
    {
        return Constants.HttpDownloadEndpointWithParams
            .Replace(Constants.HttpIndexPlaceholder, index, StringComparison.Ordinal)
            .Replace(Constants.HttpDocumentIdPlaceholder, documentId, StringComparison.Ordinal)
            .Replace(Constants.HttpFilenamePlaceholder, fileName, StringComparison.Ordinal);
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

        // var facts = new StringBuilder();
        List<string> facts = new();
        List<string> factCitations = new();
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

            int partitionNumber = memory.GetPartitionNumber(this._log);
            int sectionNumber = memory.GetSectionNumber();

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
                fileId: fileId,
                documentId: documentId,
                partitionNumber: partitionNumber,
                sectionNumber: sectionNumber,
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

            var factCitation = this.GetFactCitation(fact);
            if (!string.IsNullOrEmpty(factCitation))
            {
                factCitations.Add(factCitation);
            }
            facts.Add(fact);
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
                PartitionNumber = partitionNumber,
                SectionNumber = sectionNumber,
                LastUpdate = memory.GetLastUpdate(),
                Tags = memory.Tags,
            });

            // In cases where a buggy storage connector is returning too many records
            if (factsUsedCount >= this._config.MaxMatchesCount)
            {
                break;
            }
        }

        //if (factsAvailableCount > 0 && factsUsedCount == 0)
        //{
        //    this._log.LogError("Unable to inject memories in the prompt, not enough tokens available");
        //    noAnswerFound.NoResultReason = "Unable to use memories";
        //    yield return noAnswerFound;
        //    yield break;
        //}

        //if (factsUsedCount == 0)
        //{
        //    this._log.LogWarning("No memories available");
        //    noAnswerFound.NoResultReason = "No memories available";
        //    yield return noAnswerFound;
        //    yield break;
        //}

        var charsGenerated = 0;
        var prompt = string.Empty;
        var completeAnswer = new StringBuilder();
        await foreach (var chunk in this.GenerateAnswerChunk(question, facts, factCitations, context, cancellationToken, out prompt).ConfigureAwait(true))
        {
            completeAnswer.Append(chunk.GeneratedText);
            if (this._log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace) && chunk.GeneratedText.Length - charsGenerated >= 30)
            {
                charsGenerated = chunk.GeneratedText.Length;
                this._log.LogTrace("{0} chars generated", charsGenerated);
            }
            var newAnswer = new MemoryAnswer
            {
                Question = question,
                NoResult = false,
                Result = chunk.GeneratedText,
                CompletionUsage = new CompletionUsage(chunk.CompletionTokens, chunk.PromptTokens, chunk.TotalTokens)
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

    private IAsyncEnumerable<string> GenerateAnswer(string question, List<string> facts, List<string> factCitations, IContext? context, CancellationToken token, out string prompt)
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
        List<PromptSegment> promptSegments = this.GenerateAnswerPromptSegments(question, facts, factCitations, context);
        prompt = GenerateAnswerPrompt(promptSegments);

        // Generate Answer
        return this._textGenerator.CompleteChatAsync(promptSegments, options, token);
    }

    private List<PromptSegment> GenerateAnswerPromptSegments(string question, List<string> facts, List<string> factCitations, IContext? context)
    {
        List<PromptSegment> promptSegments = new();
        var systemPrompt = new StringBuilder();
        string answerPrompt = context.GetCustomRagPromptOrDefault(this._answerPrompt);

        var maxTokens = this._config.MaxAskPromptSize > 0
            ? this._config.MaxAskPromptSize
            : this._textGenerator.MaxTokenTotal;
        var tokensAvailable = maxTokens
                              - this._textGenerator.CountTokens(answerPrompt)
                              - this._textGenerator.CountTokens(question)
                              - this._config.AnswerTokens;

        // System Prompt
        string emptyAnswer = context.GetCustomEmptyAnswerTextOrDefault(this._config.EmptyAnswer);
        var prompt = context.GetCustomRagPromptOrDefault(this._answerPrompt);
        prompt = prompt.Replace("{{$notFound}}", emptyAnswer, StringComparison.OrdinalIgnoreCase);
        systemPrompt.Append(prompt);

        // Additional Prompt
        var additionalPrompt = context.GetCustomRagAdditionalPromptOrDefault(string.Empty);
        //if (!string.IsNullOrEmpty(additionalPrompt))
        //{
        //    additionalPrompt = $"\r\n\r\nAdditional Instructions:\r\n{additionalPrompt}";
        //    systemPrompt.Append(additionalPrompt);
        //}

        // Context
        systemPrompt.Append("\r\n\r\nContext:");
        if (!string.IsNullOrEmpty(additionalPrompt))
        {
            additionalPrompt = $"\r\n{additionalPrompt}";
            systemPrompt.Append(additionalPrompt);
        }

        // Append Facts
        // Summarize Facts
        var summarizedFacts = this.SummarizeFactsAsync(facts).ConfigureAwait(false).GetAwaiter().GetResult();
        //if (!string.IsNullOrEmpty(summarizedFacts.Trim()))
        //{
        //    systemPrompt.Append("\r\n" + summarizedFacts.Trim());
        //}
        if (summarizedFacts != null)
        {
            foreach (var fact in summarizedFacts.Summaries)
            {
                // Use the partition/chunk only if there's room for it
                var size = this._textGenerator.CountTokens(fact.AbstractiveSummary);
                if (size >= tokensAvailable)
                {
                    break; // Stop after reaching the max number of tokens
                }

                var summary = (!fact.AbstractiveSummary.StartsWith("==== [File", false, CultureInfo.CurrentCulture) ? "Deepak" : string.Empty) + fact.AbstractiveSummary;
                systemPrompt.Append("\r\n" + summary);
                tokensAvailable -= size;
            }
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

    private async Task<SummarizedTextResponse?> SummarizeFactsAsync(List<string> facts)
    {
        using var client = this._httpClientFactory.CreateClient();
        client.Timeout = new TimeSpan(0, 0, 600);

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5000/summarize");
        var content = new StringContent(JsonSerializer.Serialize(new SummarizedTextRequest { Texts = facts }), null, "application/json");
        request.Content = content;
        var response = await client.SendAsync(request).ConfigureAwait(false);
        // response.EnsureSuccessStatusCode();
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<SummarizedTextResponse>().ConfigureAwait(false);
        }

        return null;
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

    private IAsyncEnumerable<TextGenerationResult> GenerateAnswerChunk(string question, List<string> facts, List<string> factCitations, IContext? context, CancellationToken token, out string prompt)
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
        List<PromptSegment> promptSegments = this.GenerateAnswerPromptSegments(question, facts, factCitations, context);
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
