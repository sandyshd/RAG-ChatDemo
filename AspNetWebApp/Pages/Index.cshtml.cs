
using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using AspNetWebApp.Helpers;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Identity;
using Azure.Storage.Sas;
using System.Net.Mime;
using AspNetWebApp.Options;
using System.Web;

namespace AspNetWebApp.Pages
{
    public class IndexModel : PageModel
    {
    private readonly ILogger<IndexModel> _logger;
    private readonly AzureOpenAIOptions _openAiOptions;
    private readonly AzureSearchOptions _searchOptions;
    private readonly AzureBlobOptions _blobOptions;
    private const string ChatHistorySessionKey = "ChatHistory";
    private const string SystemPromptSessionKey = "SystemPrompt";
    private const string MaxResponseSessionKey = "MaxResponse";
    private const string TotalTokensSessionKey = "TotalTokens";
    private const string PromptTokensSessionKey = "PromptTokens";
    private const string CompletionTokensSessionKey = "CompletionTokens";

    // Token analytics properties for UI binding
    public int TotalTokens { get; set; } = 0;
    public int PromptTokens { get; set; } = 0;
    public int CompletionTokens { get; set; } = 0;

    public List<ChatMessage> ChatHistory { get; set; } = new();
    public string SystemPrompt { get; set; } = "You are an AI assistant that helps people find information.";
    public int MaxResponse { get; set; } = 800;

    [BindProperty]
    public string? UserInput { get; set; }

    [BindProperty]
    public string? SystemPromptInput { get; set; }

    [BindProperty]
    public int? MaxResponseInput { get; set; }

        public IndexModel(
            ILogger<IndexModel> logger,
            IOptions<AzureOpenAIOptions> openAiOptions,
            IOptions<AzureSearchOptions> searchOptions,
            IOptions<AzureBlobOptions> blobOptions)
        {
            _logger = logger;
            _openAiOptions = openAiOptions.Value;
            _searchOptions = searchOptions.Value;
            _blobOptions = blobOptions.Value;
        }

        // Helper to extract the blob path from a full blob URL
        public string GetBlobPathFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            // Remove container name if present (e.g., /container/ or container/)
            var containerName = _blobOptions?.ContainerName?.Trim('/') ?? "data";
            var containerPrefix1 = $"/{containerName}/";
            var containerPrefix2 = $"{containerName}/";
            var idx = url.IndexOf(containerPrefix1, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return url.Substring(idx + containerPrefix1.Length);
            idx = url.IndexOf(containerPrefix2, StringComparison.OrdinalIgnoreCase);
            if (idx == 0) return url.Substring(containerPrefix2.Length);
            // fallback: try to find after .windows.net/
            idx = url.IndexOf(".windows.net/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return url.Substring(idx + ".windows.net/".Length);
            // Remove leading slash if present
            if (url.StartsWith("/")) return url.Substring(1);
            return url;
        }

        // Proxy endpoint to stream blob images to the browser using RBAC
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> OnGetBlobImageAsync(string blobPath)
        {
            if (string.IsNullOrWhiteSpace(blobPath))
                return BadRequest("Missing blobPath");

            var blobOptions = _blobOptions;
            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{blobOptions.StorageAccountName}.blob.core.windows.net"),
                new DefaultAzureCredential());
            var containerClient = blobServiceClient.GetBlobContainerClient(blobOptions.ContainerName);
            var blobClient = containerClient.GetBlobClient(blobPath);
            try
            {
                var blobResponse = await blobClient.DownloadAsync();
                var contentType = blobResponse.Value.Details.ContentType ?? MediaTypeNames.Application.Octet;
                return File(blobResponse.Value.Content, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming blob image: {BlobPath}", blobPath);
                return NotFound();
            }
        }

        // ...rest of IndexModel...

    public void OnGet()
    {
        LoadSession();
    }

    public async Task<IActionResult> OnPost()
    {
        LoadSession();

        // Handle settings update
        if (!string.IsNullOrEmpty(SystemPromptInput))
        {
            SystemPrompt = SystemPromptInput;
            HttpContext.Session.SetString(SystemPromptSessionKey, SystemPrompt);
        }
        if (MaxResponseInput.HasValue)
        {
            MaxResponse = MaxResponseInput.Value;
            HttpContext.Session.SetInt32(MaxResponseSessionKey, MaxResponse);
        }

        // Handle chat input
        if (!string.IsNullOrWhiteSpace(UserInput))
        {
            var userMsg = new ChatMessage
            {
                Role = "User",
                Content = UserInput,
                AvatarUrl = "/avatars/avatar_user.png"
            };
            ChatHistory.Add(userMsg);

            // --- OPTIMIZED Azure OpenAI RAG integration using REST API ---
            string aiResponse = "[AI response placeholder]";
            List<Citation> citations = new();
            int totalTokens = 0, promptTokens = 0, completionTokens = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!string.IsNullOrEmpty(_openAiOptions.Endpoint) && !string.IsNullOrEmpty(_openAiOptions.Key) && !string.IsNullOrEmpty(_openAiOptions.Deployment))
            {
                try
                {
                    // Use a static/singleton HttpClient for performance
                    var httpClient = HttpClientSingleton.Instance;
                    httpClient.DefaultRequestHeaders.Remove("api-key");
                    httpClient.DefaultRequestHeaders.Add("api-key", _openAiOptions.Key);

                    var requestBody = new
                    {
                        messages = new[]
                        {
                            new { role = "system", content = SystemPrompt },
                            new { role = "user", content = UserInput ?? string.Empty }
                        },
                        max_tokens = MaxResponse,
                        temperature = 0.0f,
                        data_sources = new[]
                        {
                            new
                            {
                                type = "azure_search",
                                parameters = new
                                {
                                    endpoint = _searchOptions.Endpoint,
                                    index_name = _searchOptions.Index,
                                    authentication = new
                                    {
                                        type = "api_key",
                                        key = _searchOptions.ApiKey
                                    },
                                    semantic_configuration = _searchOptions.SemanticConfiguration,
                                    query_type = _searchOptions.QueryType ?? "simple",
                                    in_scope = true,
                                    strictness = 3,
                                    top_n_documents = 5
                                }
                            }
                        }
                    };

                    var jsonContent = new StringContent(
                        JsonSerializer.Serialize(requestBody),
                        Encoding.UTF8,
                        "application/json"
                    );

                    var apiUrl = $"{_openAiOptions.Endpoint.TrimEnd('/')}/openai/deployments/{_openAiOptions.Deployment}/chat/completions?api-version={_openAiOptions.ApiVersion}";
                    // Add timeout for the request
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var response = await httpClient.PostAsync(apiUrl, jsonContent, cts.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

                        if (jsonResponse.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var choice = choices[0];
                                if (choice.TryGetProperty("message", out var message) &&
                                    message.TryGetProperty("content", out var content))
                                {
                                    aiResponse = content.GetString() ?? "[No response]";
                                }
                            
                            // Parse usage information
                            if (jsonResponse.TryGetProperty("usage", out var usage))
                            {
                                if (usage.TryGetProperty("total_tokens", out var total))
                                    totalTokens = total.GetInt32();
                                if (usage.TryGetProperty("prompt_tokens", out var prompt))
                                    promptTokens = prompt.GetInt32();
                                if (usage.TryGetProperty("completion_tokens", out var completion))
                                    completionTokens = completion.GetInt32();
                                // Set public properties for UI binding
                                TotalTokens = totalTokens;
                                PromptTokens = promptTokens;
                                CompletionTokens = completionTokens;
                            }

                            // --- BEGIN FULL CITATION PARSING IMPLEMENTATION ---
                            // Parse citations from message context if present (Azure OpenAI with data sources format)
                            if (message.TryGetProperty("context", out var context))
                            {
                                // Try to parse citations from context.messages array
                                if (context.TryGetProperty("messages", out var contextMessages) && contextMessages.GetArrayLength() > 0)
                                {
                                    foreach (var contextMsg in contextMessages.EnumerateArray())
                                    {
                                        if (contextMsg.TryGetProperty("content", out var ctxContent) &&
                                            contextMsg.TryGetProperty("role", out var ctxRole) &&
                                            ctxRole.GetString() == "tool")
                                        {
                                            var citation = new Citation
                                            {
                                                Title = contextMsg.TryGetProperty("name", out var name) ? name.GetString() : "Document",
                                                Snippet = ctxContent.GetString(),
                                                FilePath = contextMsg.TryGetProperty("name", out var fileName) ? fileName.GetString() : null
                                            };
                                            citations.Add(citation);
                                        }
                                    }
                                }

                                // Also try direct citations array format
                                if (context.TryGetProperty("citations", out var citationsElement))
                                {
                                    try
                                    {
                                        foreach (var citationEl in citationsElement.EnumerateArray())
                                        {
                                            var citation = new Citation
                                            {
                                                Title = citationEl.TryGetProperty("title", out var title) ? title.GetString() : null,
                                                FilePath = citationEl.TryGetProperty("filepath", out var filepath) ? filepath.GetString() :
                                                          citationEl.TryGetProperty("url", out var url) ? url.GetString() : null,
                                                Snippet = citationEl.TryGetProperty("content", out var citationContent) ? citationContent.GetString() : null
                                            };

                                            // Enhanced image URL extraction (flatten JSON array strings)
                                            void AddImageUrlIfValid(string url)
                                            {
                                                if (string.IsNullOrWhiteSpace(url)) return;
                                                // If url looks like a JSON array, flatten it
                                                if (url.TrimStart().StartsWith("["))
                                                {
                                                    try
                                                    {
                                                        var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(url);
                                                        if (arr != null)
                                                        {
                                                            foreach (var u in arr)
                                                            {
                                                                if (!string.IsNullOrWhiteSpace(u) && (u.Contains(".jpg") || u.Contains(".png") || u.Contains(".jpeg") || u.Contains(".gif") || u.Contains(".webp") || u.Contains(".bmp") || u.Contains("blob.core.windows.net")))
                                                                    citation.ImageUrls.Add(GenerateBlobImageUrl(u));
                                                            }
                                                        }
                                                    }
                                                    catch { }
                                                }
                                                else if (url.Contains(".jpg") || url.Contains(".png") || url.Contains(".jpeg") || url.Contains(".gif") || url.Contains(".webp") || url.Contains(".bmp") || url.Contains("blob.core.windows.net"))
                                                {
                                                    citation.ImageUrls.Add(GenerateBlobImageUrl(url));
                                                }
                                            }

                                            // Check for 'url' field first
                                            if (citationEl.TryGetProperty("url", out var imageUrl))
                                            {
                                                AddImageUrlIfValid(imageUrl.GetString());
                                            }

                                            // Check for 'image_url' field
                                            if (citationEl.TryGetProperty("image_url", out var imgUrl))
                                            {
                                                AddImageUrlIfValid(imgUrl.GetString());
                                            }

                                            // Check for 'images' array
                                            if (citationEl.TryGetProperty("images", out var imagesArray))
                                            {
                                                foreach (var img in imagesArray.EnumerateArray())
                                                {
                                                    AddImageUrlIfValid(img.GetString());
                                                }
                                            }

                                            // If no images found but we have a document URL, generate a placeholder image
                                            if (citation.ImageUrls.Count == 0 && !string.IsNullOrEmpty(citation.FilePath))
                                            {
                                                var docPath = citation.FilePath;
                                                // Only generate placeholder image if not in a 'text' folder
                                                if (!docPath.Contains("/text/", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    // Handle full URLs vs relative paths
                                                    if (docPath.StartsWith("http"))
                                                    {
                                                        var possibleImageUrl = docPath.Replace(".json", ".png")
                                                                                      .Replace(".txt", ".png")
                                                                                      .Replace(".md", ".png")
                                                                                      .Replace(".pdf", ".png");
                                                        if (possibleImageUrl != docPath)
                                                        {
                                                            AddImageUrlIfValid(possibleImageUrl);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        var possibleImagePath = docPath.Replace(".json", ".png")
                                                                                      .Replace(".txt", ".png")
                                                                                      .Replace(".md", ".png")
                                                                                      .Replace(".pdf", ".png");
                                                        if (possibleImagePath != docPath)
                                                        {
                                                            AddImageUrlIfValid(possibleImagePath);
                                                        }
                                                    }
                                                }
                                            }

                                            citations.Add(citation);
                                        }
                                    }
                                    catch { }
                                }
                            }
                            // --- END FULL CITATION PARSING IMPLEMENTATION ---
                        }
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        aiResponse = $"[Error: HTTP {response.StatusCode} - {errorContent}]";
                    }
                }
                catch (Exception ex)
                {
                    aiResponse = $"[Error calling Azure OpenAI: {ex.Message}]";
                }
            }
            sw.Stop();
            _logger?.LogInformation($"OpenAI+Search request took {sw.ElapsedMilliseconds} ms");

            var aiMsg = new ChatMessage
            {
                Role = "AI",
                Content = aiResponse,
                AvatarUrl = "/avatars/avatar_ai.png",
                Citations = citations
            };
            ChatHistory.Add(aiMsg);

            SaveSession();
        }
        return RedirectToPage();
    }

    // Helper: Generate Azure Blob image URL using RBAC (DefaultAzureCredential)
    private string GenerateBlobImageUrl(string blobUrl)
    {
        // Use Azure Identity (RBAC) only. SAS is disabled. Remove hardcoded container/blob names.
        try
        {
            // If not a full URL, treat as relative path and build full URL using configured storage account and container
            if (!blobUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Use configured storage account and container from _blobOptions
                var account = _blobOptions.StorageAccountName ?? "glenfarnedemo";
                var container = _blobOptions.ContainerName ?? "documents";
                var baseUrl = $"https://{account}.blob.core.windows.net";
                // Remove any leading slashes from blobUrl
                var cleanBlobPath = blobUrl.TrimStart('/');
                blobUrl = $"{baseUrl}/{container}/{cleanBlobPath}";
            }

            // At this point, blobUrl is a full URL to the blob
            // We do NOT generate a SAS token, just return the direct URL for RBAC/Identity access
            // The app and user must have Storage Blob Data Reader role on the container
            return blobUrl;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Failed to generate blob URL for {blobUrl}: {ex.Message}");
            return blobUrl;
        }
    }

    private void LoadSession()
    {
        var chatJson = HttpContext.Session.GetString(ChatHistorySessionKey);
        if (!string.IsNullOrEmpty(chatJson))
        {
            ChatHistory = JsonSerializer.Deserialize<List<ChatMessage>>(chatJson) ?? new();
        }
        SystemPrompt = HttpContext.Session.GetString(SystemPromptSessionKey) ?? SystemPrompt;
        MaxResponse = HttpContext.Session.GetInt32(MaxResponseSessionKey) ?? MaxResponse;
        // Load token analytics from session
        TotalTokens = HttpContext.Session.GetInt32(TotalTokensSessionKey) ?? 0;
        PromptTokens = HttpContext.Session.GetInt32(PromptTokensSessionKey) ?? 0;
        CompletionTokens = HttpContext.Session.GetInt32(CompletionTokensSessionKey) ?? 0;
    }

    private void SaveSession()
    {
        HttpContext.Session.SetString(ChatHistorySessionKey, JsonSerializer.Serialize(ChatHistory));
        HttpContext.Session.SetString(SystemPromptSessionKey, SystemPrompt);
        HttpContext.Session.SetInt32(MaxResponseSessionKey, MaxResponse);
        // Save token analytics to session
        HttpContext.Session.SetInt32(TotalTokensSessionKey, TotalTokens);
        HttpContext.Session.SetInt32(PromptTokensSessionKey, PromptTokens);
        HttpContext.Session.SetInt32(CompletionTokensSessionKey, CompletionTokens);
    }

    public class ChatMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
        public string HtmlContent => MarkdownHelper.ToHtml(Content);
        public string? AvatarUrl { get; set; }
        public List<Citation> Citations { get; set; } = new();
    }

    public class Citation
    {
        public string? Title { get; set; }
        public string? FilePath { get; set; }
        public string? Snippet { get; set; }
        public List<string> ImageUrls { get; set; } = new();
    }
}

// Singleton HttpClient for performance
public class HttpClientSingleton
{
    private static readonly Lazy<HttpClient> lazy = new(() =>
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    });
    public static HttpClient Instance => lazy.Value;
}
}
