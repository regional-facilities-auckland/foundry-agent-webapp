using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using WebApp.Api.Models;

namespace WebApp.Api.Services;

#pragma warning disable OPENAI001

/// <summary>
/// Azure AI Foundry agent service using v2 Agents API.
/// </summary>
/// <remarks>
/// Uses Microsoft.Agents.AI.AzureAI extension methods on AIProjectClient for agent loading,
/// and direct ProjectResponsesClient for streaming (required for annotations, MCP approvals).
/// See .github/skills/researching-azure-ai-sdk/SKILL.md for SDK patterns.
/// </remarks>
public class AgentFrameworkService : IDisposable
{
    private readonly AIProjectClient _projectClient;
    private readonly ILogger<AgentFrameworkService> _logger;
    private readonly Dictionary<string, ChatClientAgent> _agentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentMetadataResponse> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _agentLock = new(1, 1);
    private bool _disposed = false;
    private ResponseTokenUsage? _lastUsage;

    public AgentFrameworkService(
        IConfiguration configuration,
        ILogger<AgentFrameworkService> logger)
    {
        _logger = logger;

        var endpoint = configuration["AI_AGENT_ENDPOINT"]
            ?? throw new InvalidOperationException("AI_AGENT_ENDPOINT is not configured");

        _logger.LogDebug(
            "Initializing AgentFrameworkService: endpoint={Endpoint}",
            endpoint);

        TokenCredential credential;
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";

        if (environment == "Development")
        {
            // In Development, try service principal first, then fall back to CLI credentials
            var clientId = configuration["AZURE_CLIENT_ID"];
            var tenantId = configuration["AZURE_TENANT_ID"];
            var clientSecret = configuration["AZURE_CLIENT_SECRET"];

            if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(clientSecret))
            {
                _logger.LogInformation("Development: Using ClientSecretCredential (Service Principal)");
                credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            }
            else
            {
                _logger.LogInformation("Development: Using ChainedTokenCredential (AzureCli -> AzureDeveloperCli). " +
                    "To use service principal, set AZURE_CLIENT_ID, AZURE_TENANT_ID, and AZURE_CLIENT_SECRET");
                credential = new ChainedTokenCredential(
                    new AzureCliCredential(),
                    new AzureDeveloperCliCredential()
                );
            }
        }
        else
        {
            _logger.LogInformation("Production: Using ManagedIdentityCredential (system-assigned)");
            credential = new ManagedIdentityCredential();
        }

        var networkTimeoutSeconds = configuration.GetValue<int?>("AI_AGENT_NETWORK_TIMEOUT_SECONDS") ?? 600;
        if (networkTimeoutSeconds <= 0)
        {
            networkTimeoutSeconds = 600;
        }

        var clientOptions = new AIProjectClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds)
        };

        var apimSubscriptionKey = configuration["AI_AGENT_APIM_SUBSCRIPTION_KEY"]
            ?? configuration["APIM_SUBSCRIPTION_KEY"];

        if (!string.IsNullOrWhiteSpace(apimSubscriptionKey))
        {
            var apimPolicy = ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(
                credential: new ApiKeyCredential(apimSubscriptionKey),
                headerName: "api-key",
                keyPrefix: null);

            clientOptions.AddPolicy(
                apimPolicy,
                PipelinePosition.PerCall);

            _logger.LogInformation(
                "Configured APIM subscription key policy for AI agent endpoint");
        }
        else if (endpoint.Contains(".azure-api.net", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "AI_AGENT_ENDPOINT appears to use APIM, but no subscription key is configured. " +
                "Set AI_AGENT_APIM_SUBSCRIPTION_KEY (or APIM_SUBSCRIPTION_KEY) to avoid 401 errors.");
        }

        _projectClient = new AIProjectClient(new Uri(endpoint), credential, clientOptions);
        _logger.LogInformation(
            "Configured AIProjectClient network timeout: {TimeoutSeconds}s",
            networkTimeoutSeconds);
        _logger.LogInformation("AIProjectClient initialized successfully");
    }

    /// <summary>
    /// Get agent via Microsoft Agent Framework extension methods.
    /// Uses AIProjectClient.GetAIAgentAsync() which wraps v2 Agents API.
    /// </summary>
    private async Task<ChatClientAgent> GetAgentAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent id must be provided", nameof(agentId));

        if (_agentCache.TryGetValue(agentId, out var cachedAgent))
            return cachedAgent;

        await _agentLock.WaitAsync(cancellationToken);
        try
        {
            if (_agentCache.TryGetValue(agentId, out cachedAgent))
                return cachedAgent;

            _logger.LogInformation("Loading agent via Agent Framework: {AgentId}", agentId);

            // Use Microsoft.Agents.AI.AzureAI extension method - handles v2 Agents API internally
            var loadedAgent = await _projectClient.GetAIAgentAsync(
                name: agentId,
                cancellationToken: cancellationToken);

            // Get the AgentVersion from the cached agent for metadata
            var agentVersion = loadedAgent.GetService<AgentVersion>();
            var definition = agentVersion?.Definition as PromptAgentDefinition;
            
            _logger.LogInformation(
                "Loaded agent: name={AgentName}, model={Model}, version={Version}", 
                agentVersion?.Name ?? agentId,
                definition?.Model ?? "unknown",
                agentVersion?.Version ?? "latest");

            // Log StructuredInputs at debug level for troubleshooting
            if (definition?.StructuredInputs != null && definition.StructuredInputs.Count > 0)
            {
                _logger.LogDebug("Agent has {Count} StructuredInputs: {Keys}", 
                    definition.StructuredInputs.Count, 
                    string.Join(", ", definition.StructuredInputs.Keys));
            }

            _agentCache[agentId] = loadedAgent;
            return loadedAgent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load agent: {AgentId}", agentId);
            throw;
        }
        finally
        {
            _agentLock.Release();
        }
    }

    /// <summary>
    /// Streams agent response for a message using ProjectResponsesClient (Responses API).
    /// Returns StreamChunk objects containing text deltas, annotations, or MCP approval requests.
    /// </summary>
    /// <remarks>
    /// Uses direct ProjectResponsesClient instead of IChatClient because we need access to:
    /// - McpToolCallApprovalRequestItem for MCP approval flows
    /// - FileSearchCallResponseItem for file search quotes  
    /// - MessageResponseItem.OutputTextAnnotations for citations
    /// The IChatClient abstraction doesn't expose these specialized response types.
    /// </remarks>
    public async IAsyncEnumerable<StreamChunk> StreamMessageAsync(
        string agentId,
        string conversationId,
        string message,
        List<string>? imageDataUris = null,
        List<FileAttachment>? fileDataUris = null,
        string? previousResponseId = null,
        McpApprovalResponse? mcpApproval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent id must be provided", nameof(agentId));

        _logger.LogInformation(
            "Streaming message to agent: {AgentId}, conversation: {ConversationId}, ImageCount: {ImageCount}, FileCount: {FileCount}, HasApproval: {HasApproval}",
            agentId,
            conversationId,
            imageDataUris?.Count ?? 0,
            fileDataUris?.Count ?? 0,
            mcpApproval != null);

        // Get ProjectResponsesClient for the agent and conversation
        ProjectResponsesClient responsesClient
            = _projectClient.OpenAI.GetProjectResponsesClientForAgent(
                new AgentReference(agentId), 
                conversationId);

        CreateResponseOptions options = new() { StreamingEnabled = true };

        // If continuing from MCP approval, send approval input item.
        // previousResponseId is optional in conversation-scoped flows.
        if (mcpApproval != null)
        {
            options.InputItems.Add(ResponseItem.CreateMcpApprovalResponseItem(
                mcpApproval.ApprovalRequestId,
                mcpApproval.Approved));

            // Conversation-scoped responses already carry conversation context.
            // Sending PreviousResponseId together with conversation context causes
            // invalid_payload errors from the Responses API.
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                options.PreviousResponseId = previousResponseId;
            }
            
            _logger.LogInformation(
                "Resuming with MCP approval: RequestId={RequestId}, Approved={Approved}, HasPreviousResponseId={HasPreviousResponseId}, UsingPreviousResponseId={UsingPreviousResponseId}",
                mcpApproval.ApprovalRequestId,
                mcpApproval.Approved,
                !string.IsNullOrWhiteSpace(previousResponseId),
                string.IsNullOrWhiteSpace(conversationId));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                _logger.LogWarning("Attempted to stream empty message to conversation {ConversationId}", conversationId);
                throw new ArgumentException("Message cannot be null or whitespace", nameof(message));
            }

            // Build user message with optional images and files
            ResponseItem userMessage = BuildUserMessage(message, imageDataUris, fileDataUris);
            options.InputItems.Add(userMessage);
        }

        // Dictionary to collect file search results for quote extraction
        var fileSearchQuotes = new Dictionary<string, string>();

        await foreach (StreamingResponseUpdate update
            in responsesClient.CreateResponseStreamingAsync(
                options: options,
                cancellationToken: cancellationToken))
        {
            if (update is StreamingResponseOutputTextDeltaUpdate deltaUpdate)
            {
                yield return StreamChunk.Text(deltaUpdate.Delta);
            }
            else if (update is StreamingResponseOutputItemDoneUpdate itemDoneUpdate)
            {
                // Check for MCP tool approval request
                if (itemDoneUpdate.Item is McpToolCallApprovalRequestItem mcpApprovalItem)
                {
                    _logger.LogInformation(
                        "MCP tool approval requested: Id={Id}, Tool={Tool}, Server={Server}",
                        mcpApprovalItem.Id,
                        mcpApprovalItem.ToolName,
                        mcpApprovalItem.ServerLabel);
                    
                    // Parse tool arguments from BinaryData to string (JSON)
                    string? argumentsJson = mcpApprovalItem.ToolArguments?.ToString();
                    
                    yield return StreamChunk.McpApproval(new McpApprovalRequest
                    {
                        Id = mcpApprovalItem.Id,
                        ToolName = mcpApprovalItem.ToolName ?? "Unknown tool",
                        ServerLabel = mcpApprovalItem.ServerLabel ?? "MCP Server",
                        Arguments = argumentsJson
                    });
                    continue;
                }
                
                // Capture file search results for quote extraction
                if (itemDoneUpdate.Item is FileSearchCallResponseItem fileSearchItem)
                {
                    foreach (var result in fileSearchItem.Results)
                    {
                        if (!string.IsNullOrEmpty(result.FileId) && !string.IsNullOrEmpty(result.Text))
                        {
                            fileSearchQuotes[result.FileId] = result.Text;
                            _logger.LogDebug(
                                "Captured file search quote for FileId={FileId}, QuoteLength={Length}", 
                                result.FileId, 
                                result.Text.Length);
                        }
                    }
                    continue;
                }
                
                // Extract annotations/citations from completed output items
                var annotations = ExtractAnnotations(itemDoneUpdate.Item, fileSearchQuotes);
                if (annotations.Count > 0)
                {
                    _logger.LogInformation("Extracted {Count} annotations from response", annotations.Count);
                    yield return StreamChunk.WithAnnotations(annotations);
                }
            }
            else if (update is StreamingResponseCompletedUpdate completedUpdate)
            {
                _lastUsage = completedUpdate.Response.Usage;
            }
            else if (update is StreamingResponseErrorUpdate errorUpdate)
            {
                _logger.LogError("Stream error: {Error}", errorUpdate.Message);
                throw new InvalidOperationException($"Stream error: {errorUpdate.Message}");
            }
        }

        _logger.LogInformation("Completed streaming for agent: {AgentId}, conversation: {ConversationId}", agentId, conversationId);
    }

    /// <summary>
    /// Supported image MIME types for vision capabilities.
    /// </summary>
    private static readonly HashSet<string> AllowedImageTypes = 
        ["image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp"];

    /// <summary>
    /// Supported document MIME types for file input.
    /// Note: Office documents (docx, pptx, xlsx) are NOT supported - they cannot be parsed.
    /// </summary>
    private static readonly HashSet<string> AllowedDocumentTypes = 
        [
            "application/pdf",
            "text/plain",
            "text/markdown",
            "text/csv",
            "application/json",
            "text/html",
            "application/xml",
            "text/xml"
        ];

    /// <summary>
    /// Text-based document MIME types that should be inlined as text rather than sent as file input.
    /// The Responses API only supports PDF for CreateInputFilePart.
    /// </summary>
    private static readonly HashSet<string> TextBasedDocumentTypes = 
        [
            "text/plain",
            "text/markdown",
            "text/csv",
            "application/json",
            "text/html",
            "application/xml",
            "text/xml"
        ];

    /// <summary>
    /// MIME types that can be sent as file input (only PDF is currently supported by Responses API).
    /// </summary>
    private static readonly HashSet<string> FileInputTypes = 
        [
            "application/pdf"
        ];

    /// <summary>
    /// Maximum number of images per message.
    /// </summary>
    private const int MaxImageCount = 5;

    /// <summary>
    /// Maximum number of files per message.
    /// </summary>
    private const int MaxFileCount = 10;

    /// <summary>
    /// Maximum size per image in bytes (5MB).
    /// </summary>
    private const long MaxImageSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Maximum size per document file in bytes (20MB).
    /// </summary>
    private const long MaxFileSizeBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Builds a ResponseItem for the user message with optional image and file attachments.
    /// Validates count, size, MIME type, and Base64 format for both images and documents.
    /// </summary>
    private static ResponseItem BuildUserMessage(
        string message, 
        List<string>? imageDataUris,
        List<FileAttachment>? fileDataUris = null)
    {
        if ((imageDataUris == null || imageDataUris.Count == 0) && 
            (fileDataUris == null || fileDataUris.Count == 0))
        {
            return ResponseItem.CreateUserMessageItem(message);
        }

        var contentParts = new List<ResponseContentPart>
        {
            ResponseContentPart.CreateInputTextPart(message)
        };

        var errors = new List<string>();

        // Process images
        if (imageDataUris != null && imageDataUris.Count > 0)
        {
            // Enforce maximum image count
            if (imageDataUris.Count > MaxImageCount)
            {
                throw new ArgumentException(
                    $"Invalid image attachments: Too many images ({imageDataUris.Count}), maximum {MaxImageCount} allowed");
            }

            for (int i = 0; i < imageDataUris.Count; i++)
            {
                var dataUri = imageDataUris[i];
                
                // Validate data URI format
                if (!dataUri.StartsWith("data:"))
                {
                    errors.Add($"Image {i + 1}: Invalid format (must be data URI)");
                    continue;
                }

                var semiIndex = dataUri.IndexOf(';');
                var commaIndex = dataUri.IndexOf(',');
                
                if (semiIndex < 0 || commaIndex < 0 || commaIndex < semiIndex)
                {
                    errors.Add($"Image {i + 1}: Malformed data URI");
                    continue;
                }

                // Extract and validate MIME type
                var mediaType = dataUri[5..semiIndex].ToLowerInvariant();
                if (!AllowedImageTypes.Contains(mediaType))
                {
                    errors.Add($"Image {i + 1}: Unsupported type '{mediaType}'. Allowed: PNG, JPEG, GIF, WebP");
                    continue;
                }

                // Validate Base64 and decode
                var base64Data = dataUri[(commaIndex + 1)..];
                try
                {
                    var bytes = Convert.FromBase64String(base64Data);
                    
                    // Enforce size limit
                    if (bytes.Length > MaxImageSizeBytes)
                    {
                        var sizeMB = bytes.Length / (1024.0 * 1024.0);
                        errors.Add($"Image {i + 1}: Size {sizeMB:F1}MB exceeds maximum 5MB");
                        continue;
                    }
                    
                    contentParts.Add(ResponseContentPart.CreateInputImagePart(
                        BinaryData.FromBytes(bytes),
                        mediaType));
                }
                catch (FormatException)
                {
                    errors.Add($"Image {i + 1}: Invalid Base64 encoding");
                }
            }
        }

        // Process file attachments
        if (fileDataUris != null && fileDataUris.Count > 0)
        {
            // Enforce maximum file count
            if (fileDataUris.Count > MaxFileCount)
            {
                throw new ArgumentException(
                    $"Invalid file attachments: Too many files ({fileDataUris.Count}), maximum {MaxFileCount} allowed");
            }

            for (int i = 0; i < fileDataUris.Count; i++)
            {
                var file = fileDataUris[i];
                var dataUri = file.DataUri;
                
                // Validate data URI format
                if (!dataUri.StartsWith("data:"))
                {
                    errors.Add($"File {i + 1} ({file.FileName}): Invalid format (must be data URI)");
                    continue;
                }

                var semiIndex = dataUri.IndexOf(';');
                var commaIndex = dataUri.IndexOf(',');
                
                if (semiIndex < 0 || commaIndex < 0 || commaIndex < semiIndex)
                {
                    errors.Add($"File {i + 1} ({file.FileName}): Malformed data URI");
                    continue;
                }

                // Extract and validate MIME type
                var mediaType = dataUri[5..semiIndex].ToLowerInvariant();
                if (!AllowedDocumentTypes.Contains(mediaType))
                {
                    errors.Add($"File {i + 1} ({file.FileName}): Unsupported type '{mediaType}'");
                    continue;
                }

                // Verify MIME type matches what was declared
                if (!string.Equals(mediaType, file.MimeType.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"File {i + 1} ({file.FileName}): MIME type mismatch (declared: {file.MimeType}, detected: {mediaType})");
                    continue;
                }

                // Validate Base64 and decode
                var base64Data = dataUri[(commaIndex + 1)..];
                try
                {
                    var bytes = Convert.FromBase64String(base64Data);
                    
                    // Enforce size limit
                    if (bytes.Length > MaxFileSizeBytes)
                    {
                        var sizeMB = bytes.Length / (1024.0 * 1024.0);
                        errors.Add($"File {i + 1} ({file.FileName}): Size {sizeMB:F1}MB exceeds maximum 20MB");
                        continue;
                    }
                    
                    // Handle text-based files by inlining their content
                    // The Responses API only supports PDF for CreateInputFilePart
                    if (TextBasedDocumentTypes.Contains(mediaType))
                    {
                        // Decode text content and add as inline text with filename context
                        var textContent = System.Text.Encoding.UTF8.GetString(bytes);
                        var inlineText = $"\n\n--- Content of {file.FileName} ---\n{textContent}\n--- End of {file.FileName} ---\n";
                        contentParts.Add(ResponseContentPart.CreateInputTextPart(inlineText));
                    }
                    else if (FileInputTypes.Contains(mediaType))
                    {
                        // PDF files can be sent as file input
                        contentParts.Add(ResponseContentPart.CreateInputFilePart(
                            BinaryData.FromBytes(bytes),
                            mediaType,
                            file.FileName));
                    }
                }
                catch (FormatException)
                {
                    errors.Add($"File {i + 1} ({file.FileName}): Invalid Base64 encoding");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid attachments: {string.Join("; ", errors)}");
        }

        return ResponseItem.CreateUserMessageItem(contentParts);
    }

    /// <summary>
    /// Extracts annotation information from a completed response item.
    /// </summary>
    private List<AnnotationInfo> ExtractAnnotations(
        ResponseItem? item, 
        Dictionary<string, string>? fileSearchQuotes = null)
    {
        var annotations = new List<AnnotationInfo>();
        
        if (item is not MessageResponseItem messageItem)
            return annotations;

        foreach (var content in messageItem.Content)
        {
            if (content.OutputTextAnnotations == null) continue;
            
            foreach (var annotation in content.OutputTextAnnotations)
            {
                var annotationInfo = annotation switch
                {
                    UriCitationMessageAnnotation uriAnnotation => new AnnotationInfo
                    {
                        Type = "uri_citation",
                        Label = uriAnnotation.Title ?? "Source",
                        Url = uriAnnotation.Uri?.ToString(),
                        StartIndex = uriAnnotation.StartIndex,
                        EndIndex = uriAnnotation.EndIndex
                    },
                    
                    FileCitationMessageAnnotation fileCitation => new AnnotationInfo
                    {
                        Type = "file_citation",
                        Label = fileCitation.Filename ?? fileCitation.FileId ?? "File",
                        FileId = fileCitation.FileId,
                        StartIndex = fileCitation.Index,
                        EndIndex = fileCitation.Index,
                        Quote = fileSearchQuotes?.TryGetValue(fileCitation.FileId ?? string.Empty, out var quote) == true 
                            ? quote : null
                    },
                    
                    FilePathMessageAnnotation filePath => new AnnotationInfo
                    {
                        Type = "file_path",
                        Label = "Generated File",
                        FileId = filePath.FileId,
                        StartIndex = filePath.Index,
                        EndIndex = filePath.Index
                    },
                    
                    ContainerFileCitationMessageAnnotation containerCitation => new AnnotationInfo
                    {
                        Type = "container_file_citation",
                        Label = containerCitation.Filename ?? "Container File",
                        FileId = containerCitation.FileId,
                        StartIndex = containerCitation.StartIndex,
                        EndIndex = containerCitation.EndIndex,
                        Quote = fileSearchQuotes?.TryGetValue(containerCitation.FileId, out var containerQuote) == true 
                            ? containerQuote : null
                    },
                    
                    _ => null
                };
                
                if (annotationInfo != null)
                    annotations.Add(annotationInfo);
            }
        }

        return annotations;
    }

    /// <summary>
    /// Create a new conversation for the agent.
    /// Uses ProjectConversation from Azure.AI.Projects for server-managed state.
    /// </summary>
    public async Task<string> CreateConversationAsync(string? firstMessage = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _logger.LogInformation("Creating new conversation");
            
            ProjectConversationCreationOptions conversationOptions = new();

            if (!string.IsNullOrEmpty(firstMessage))
            {
                // Store title in metadata (truncate to 50 chars)
                var title = firstMessage.Length > 50 
                    ? firstMessage[..50] + "..."
                    : firstMessage;
                conversationOptions.Metadata["title"] = title;
            }

            ProjectConversation conversation
                = await _projectClient.OpenAI.Conversations.CreateProjectConversationAsync(
                    conversationOptions,
                    cancellationToken);

            _logger.LogInformation(
                "Created conversation: {ConversationId}", 
                conversation.Id);
            return conversation.Id;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Conversation creation was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create conversation");
            throw;
        }
    }

    /// <summary>
    /// Get the agent metadata (name, description, etc.) for display in UI.
    /// Uses Agent Framework's ChatClientAgent which provides access to AgentVersion.
    /// </summary>
    public async Task<AgentMetadataResponse> GetAgentMetadataAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent id must be provided", nameof(agentId));

        if (_metadataCache.TryGetValue(agentId, out var cachedMetadata))
            return cachedMetadata;

        _logger.LogInformation("Loading agent metadata for requested id: {AgentId}", agentId);
        var agent = await GetAgentAsync(agentId, cancellationToken);
        var metadataResponse = BuildAgentMetadataResponse(agent, agentId);

        _metadataCache[agentId] = metadataResponse;
        return metadataResponse;
    }

    private AgentMetadataResponse BuildAgentMetadataResponse(ChatClientAgent agent, string agentId)
    {
        var agentVersion = agent.GetService<AgentVersion>();
        if (agentVersion == null)
            throw new InvalidOperationException("Agent version not available from ChatClientAgent");

        var definition = agentVersion.Definition as PromptAgentDefinition;
        var metadata = agentVersion.Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        if (metadata != null && metadata.Count > 0)
        {
            _logger.LogDebug("Agent metadata keys: {Keys}", string.Join(", ", metadata.Keys));
        }

        List<string>? starterPrompts = ParseStarterPrompts(metadata);

        return new AgentMetadataResponse
        {
            Id = agentId,
            Object = "agent",
            CreatedAt = agentVersion.CreatedAt.ToUnixTimeSeconds(),
            Name = agentVersion.Name ?? "AI Assistant",
            Description = agentVersion.Description ?? string.Empty,
            Version = agentVersion.Version ?? string.Empty,
            Model = definition?.Model ?? string.Empty,
            Instructions = definition?.Instructions ?? string.Empty,
            Metadata = metadata,
            StarterPrompts = starterPrompts
        };
    }

    /// <summary>
    /// Parse starter prompts from agent metadata.
    /// Azure AI Foundry stores starter prompts as newline-separated text in the "starterPrompts" metadata key.
    /// Example: "How's the weather?\nIs your fridge running?\nTell me a joke"
    /// </summary>
    private List<string>? ParseStarterPrompts(Dictionary<string, string>? metadata)
    {
        if (metadata == null)
            return null;

        // Azure AI Foundry uses camelCase "starterPrompts" key with newline-separated values
        if (!metadata.TryGetValue("starterPrompts", out var starterPromptsValue))
            return null;

        if (string.IsNullOrWhiteSpace(starterPromptsValue))
            return null;

        // Split by newlines and filter out empty entries
        var prompts = starterPromptsValue
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        if (prompts.Count > 0)
        {
            _logger.LogDebug("Parsed {Count} starter prompts from agent metadata", prompts.Count);
            return prompts;
        }

        return null;
    }

    /// <summary>
    /// Get basic agent info string (for debugging).
    /// </summary>
    public async Task<string> GetAgentInfoAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent id must be provided", nameof(agentId));

        var agent = await GetAgentAsync(agentId, cancellationToken);
        var agentVersion = agent.GetService<AgentVersion>();
        return agentVersion?.Name ?? agentId;
    }

    /// <summary>
    /// Get token usage from the last streaming response.
    /// </summary>
    public (int InputTokens, int OutputTokens, int TotalTokens)? GetLastUsage() =>
        _lastUsage is null ? null : (_lastUsage.InputTokenCount, _lastUsage.OutputTokenCount, _lastUsage.TotalTokenCount);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _agentLock.Dispose();
            _agentCache.Clear();
            _metadataCache.Clear();
            _logger.LogDebug("AgentFrameworkService disposed");
        }
    }
}
