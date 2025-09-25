using Markdig;

namespace AspNetWebApp.Helpers
{
    public static class MarkdownHelper
    {
        public static string ToHtml(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return "";
            return Markdown.ToHtml(markdown);
        }
    }
}
