
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
using Azure.Identity;
using System.Net.Mime;
using AspNetWebApp.Options;
using System.Threading;
using System.Linq;

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

        public void OnGet()
        {
            LoadSession();
        }

        public async Task<IActionResult> OnPost()
        {
            LoadSession();
            if (!string.IsNullOrWhiteSpace(SystemPromptInput))
                SystemPrompt = SystemPromptInput;
            if (MaxResponseInput.HasValue)
                MaxResponse = MaxResponseInput.Value;

            if (!string.IsNullOrWhiteSpace(UserInput))
            {
                // Add user message to chat history
                ChatHistory.Add(new ChatMessage
                {
                    Role = "User",
                    Content = UserInput,
                    AvatarUrl = "/avatars/avatar_user.png"
                });

                // Detect language (simple heuristic)
                string detectedLanguage = DetectLanguage(UserInput);

                // Prepare request to Azure OpenAI with Azure Search data source
                var requestBody = new
                {
                    messages = new[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = UserInput }
                    },
                    max_tokens = MaxResponse,
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
                                top_n_documents = 5,
                                // query_language removed: not supported by API
                            }
                        }
                    }
                };

                string aiResponse = string.Empty;
                List<Citation> citations = new();
                int totalTokens = 0, promptTokens = 0, completionTokens = 0;
                try
                {
                    var httpClient = HttpClientSingleton.Instance;
                    // Build the correct Azure OpenAI chat completions endpoint
                    var endpoint = _openAiOptions.Endpoint?.TrimEnd('/') + "/openai/deployments/" + _openAiOptions.Deployment + "/chat/completions?api-version=" + _openAiOptions.ApiVersion;
                    var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Headers.Add("api-key", _openAiOptions.Key);
                    request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                    var response = await httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var choices = root.GetProperty("choices");
                        if (choices.GetArrayLength() > 0)
                        {
                            var message = choices[0].GetProperty("message");
                            aiResponse = message.GetProperty("content").GetString() ?? string.Empty;
                            // Token usage
                            if (root.TryGetProperty("usage", out var usage))
                            {
                                if (usage.TryGetProperty("total_tokens", out var total))
                                    totalTokens = total.GetInt32();
                                if (usage.TryGetProperty("prompt_tokens", out var prompt))
                                    promptTokens = prompt.GetInt32();
                                if (usage.TryGetProperty("completion_tokens", out var completion))
                                    completionTokens = completion.GetInt32();
                                TotalTokens = totalTokens;
                                PromptTokens = promptTokens;
                                CompletionTokens = completionTokens;
                            }
                            // Parse citations if present
                            if (message.TryGetProperty("context", out var context))
                            {
                                if (context.TryGetProperty("citations", out var citationsElement))
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
                                        // Parse image URLs from 'images', 'image_url', or 'url' fields
                                        if (citationEl.TryGetProperty("images", out var imagesArray) && imagesArray.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var img in imagesArray.EnumerateArray())
                                            {
                                                var imgUrl = img.GetString();
                                                if (!string.IsNullOrWhiteSpace(imgUrl))
                                                    citation.ImageUrls.Add(imgUrl);
                                            }
                                        }
                                        else if (citationEl.TryGetProperty("image_url", out var imageUrlProp))
                                        {
                                            var imgUrl = imageUrlProp.GetString();
                                            if (!string.IsNullOrWhiteSpace(imgUrl))
                                                citation.ImageUrls.Add(imgUrl);
                                        }
                                        else if (citationEl.TryGetProperty("url", out var urlProp))
                                        {
                                            var imgUrl = urlProp.GetString();
                                            if (!string.IsNullOrWhiteSpace(imgUrl))
                                            {
                                                if (imgUrl.TrimStart().StartsWith("["))
                                                {
                                                    // url is a JSON array string
                                                    try
                                                    {
                                                        var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(imgUrl);
                                                        if (arr != null)
                                                        {
                                                            foreach (var u in arr)
                                                            {
                                                                if (!string.IsNullOrWhiteSpace(u) && (u.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || u.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || u.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || u.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || u.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) || u.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)))
                                                                    citation.ImageUrls.Add(u);
                                                            }
                                                        }
                                                    }
                                                    catch { }
                                                }
                                                else if (imgUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || imgUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || imgUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || imgUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) || imgUrl.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) || imgUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    citation.ImageUrls.Add(imgUrl);
                                                }
                                            }
                                        }
                                        citations.Add(citation);
                                    }
                                }
                            }
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

                ChatHistory.Add(new ChatMessage
                {
                    Role = "AI",
                    Content = aiResponse,
                    AvatarUrl = "/avatars/avatar_ai.png",
                    Citations = citations
                });
                SaveSession();
            }
            return RedirectToPage();
        }

        // Helper to extract the blob path from a full blob URL
        public string GetBlobPathFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var containerName = _blobOptions?.ContainerName?.Trim('/') ?? "data";
            var containerPrefix1 = $"/{containerName}/";
            var containerPrefix2 = $"{containerName}/";
            var idx = url.IndexOf(containerPrefix1, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return url.Substring(idx + containerPrefix1.Length);
            idx = url.IndexOf(containerPrefix2, StringComparison.OrdinalIgnoreCase);
            if (idx == 0) return url.Substring(containerPrefix2.Length);
            idx = url.IndexOf(".windows.net/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return url.Substring(idx + ".windows.net/".Length);
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

        // Helper: Generate Azure Blob image URL using RBAC (DefaultAzureCredential)
        private string GenerateBlobImageUrl(string blobUrl)
        {
            try
            {
                if (!blobUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var account = _blobOptions.StorageAccountName ?? "glenfarnedemo";
                    var container = _blobOptions.ContainerName ?? "documents";
                    var baseUrl = $"https://{account}.blob.core.windows.net";
                    var cleanBlobPath = blobUrl.TrimStart('/');
                    blobUrl = $"{baseUrl}/{container}/{cleanBlobPath}";
                }
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
            TotalTokens = HttpContext.Session.GetInt32(TotalTokensSessionKey) ?? 0;
            PromptTokens = HttpContext.Session.GetInt32(PromptTokensSessionKey) ?? 0;
            CompletionTokens = HttpContext.Session.GetInt32(CompletionTokensSessionKey) ?? 0;
        }

        private void SaveSession()
        {
            HttpContext.Session.SetString(ChatHistorySessionKey, JsonSerializer.Serialize(ChatHistory));
            HttpContext.Session.SetString(SystemPromptSessionKey, SystemPrompt);
            HttpContext.Session.SetInt32(MaxResponseSessionKey, MaxResponse);
            HttpContext.Session.SetInt32(TotalTokensSessionKey, TotalTokens);
            HttpContext.Session.SetInt32(PromptTokensSessionKey, PromptTokens);
            HttpContext.Session.SetInt32(CompletionTokensSessionKey, CompletionTokens);
        }

        // Simple language detection: returns "es" for Spanish, "en" for English (default)
        private string DetectLanguage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "en";
            // Naive check for common Spanish words/characters
            string[] spanishWords = { "el", "la", "de", "que", "y", "en", "un", "ser", "se", "no", "haber", "por", "con", "su", "para", "como", "estar", "tener", "le", "lo", "lo", "más", "pero", "sus", "yo", "ya", "o", "este", "sí", "porque", "esta", "entre", "cuando", "muy", "sin", "sobre", "también", "me", "hasta", "hay", "donde", "quien", "desde", "todo", "nos", "durante", "todos", "uno", "les", "ni", "contra", "otros", "ese", "eso", "ante", "ellos", "e", "esto", "mí", "antes", "algunos", "qué", "unos", "yo", "otro", "otras", "otra", "él", "tanto", "esa", "estos", "mucho", "quienes", "nada", "muchos", "cual", "poco", "ella", "estar", "estas", "algunas", "algo", "nosotros", "mi", "mis", "tú", "te", "ti", "tu", "tus", "ellas", "nosotras", "vosotros", "vosotras", "os", "mío", "mía", "míos", "mías", "tuyo", "tuya", "tuyos", "tuyas", "suyo", "suya", "suyos", "suyas", "nuestro", "nuestra", "nuestros", "nuestras", "vuestro", "vuestra", "vuestros", "vuestras", "esos", "esas", "estoy", "estás", "está", "estamos", "estáis", "están", "esté", "estés", "estemos", "estéis", "estén", "estaré", "estarás", "estará", "estaremos", "estaréis", "estarán", "estaría", "estarías", "estaríamos", "estaríais", "estarían", "estaba", "estabas", "estábamos", "estabais", "estaban", "estuve", "estuviste", "estuvo", "estuvimos", "estuvisteis", "estuvieron", "estuviera", "estuvieras", "estuviéramos", "estuvierais", "estuvieran", "estuviese", "estuvieses", "estuviésemos", "estuvieseis", "estuviesen", "estando", "estado", "estada", "estados", "estadas", "estad" };
            int matches = spanishWords.Count(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
            if (matches > 2 || text.Any(c => "áéíóúñü¿¡".Contains(c)))
                return "es";
            return "en";
        }
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
