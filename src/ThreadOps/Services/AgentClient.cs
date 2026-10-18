using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ThreadOps.Models;

namespace ThreadOps.Services;

/// <summary>
/// Envia a conversa para o seu agente de IA e recebe a resposta sugerida.
///
/// Requisição (POST, JSON):
///   { "perfil": "usuario", "meuUsuario": "voce",
///     "post": { "url": "...", "autor": "...", "texto": "..." },
///     "conversa": [ { "autor": "usuario", "eu": false, "texto": "..." }, ... ] }
///
/// Resposta aceita: { "resposta": "..." }, { "reply": "..." }, { "text": "..." } ou texto puro.
/// </summary>
public sealed class AgentClient(AppConfig.AgentSettings settings)
{
    private static readonly HttpClient Http = new();
    private static readonly string[] ReplyFields = ["resposta", "reply", "text", "output", "message"];

    public bool IsConfigured => Uri.TryCreate(settings.Url, UriKind.Absolute, out _);

    public async Task<string> SuggestReplyAsync(string handle, string me, string postUrl, string postAuthor,
        string postText, IReadOnlyList<ChatMessage> conversation)
    {
        var payload = new
        {
            perfil = handle,
            meuUsuario = me,
            post = new { url = postUrl, autor = postAuthor, texto = postText },
            conversa = conversation.Select(m => new { autor = m.Author, eu = m.IsMine, texto = m.Text }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(settings.HeaderAutorizacao))
            request.Headers.TryAddWithoutValidation("Authorization", settings.HeaderAutorizacao);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSegundos)));
        using var response = await Http.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agente respondeu {(int)response.StatusCode}: {Truncate(body, 200)}");

        return ExtractReply(body).Trim();
    }

    private static string ExtractReply(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return doc.RootElement.GetString() ?? "";
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var field in ReplyFields)
                    if (doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString() ?? "";
            throw new InvalidOperationException("JSON do agente sem campo de resposta (esperado: \"resposta\").");
        }
        catch (JsonException)
        {
            return body; // texto puro
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
