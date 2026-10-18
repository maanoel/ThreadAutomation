using System.Text.Json;
using ThreadOps.Models;

namespace ThreadOps.Services;

public enum FillResult { Filled, CommentNotFound, ReplyButtonNotFound, ComposerNotFound }

/// <summary>
/// Conversa a partir de um comentário: lê as mensagens da página do comentário e preenche a caixa
/// de resposta. Nunca publica — o botão Publicar do Threads é sempre clicado pelo usuário.
/// </summary>
public sealed class ThreadsConversation(PageDriver page)
{
    private sealed record Item(string Author, string Text, string? Url);
    private sealed record PageData(string? Me, List<Item> Items);

    private static readonly TimeSpan ComposerTimeout = TimeSpan.FromSeconds(8);
    private const string HasPostLinksJs = "document.querySelectorAll('a[href*=\"/post/\"]').length > 0";

    private static readonly string ReadJs = "(() => {" + ThreadsScraper.HelpersJs + """
          const nav = document.querySelector('nav a[href^="/@"], [role="navigation"] a[href^="/@"]');
          const me = nav ? (nav.getAttribute('href').match(/^\/@([^/?#]+)/) || [])[1] || null : null;
          const items = [];
          for (const box of document.querySelectorAll('[data-pressable-container="true"]')) {
            const h = __author(box);
            if (h) items.push({ author: h, text: __longest(box, h), url: __url(box) });
          }
          return JSON.stringify({ me, items });
        })()
        """;

    private static string ClickReplyJs(string commentUrl) => "(() => {" + ThreadsScraper.HelpersJs + $$"""
          const target = {{JsonSerializer.Serialize(commentUrl)}};
          const box = [...document.querySelectorAll('[data-pressable-container="true"]')].find(b => __url(b) === target);
          if (!box) return 'no_box';
          const icon = box.querySelector('svg[aria-label="Responder"], svg[aria-label="Reply"], svg[aria-label="Comentar"], svg[aria-label="Comment"]');
          const btn = icon && (icon.closest('[role="button"]') || icon.parentElement);
          if (!btn) return 'no_button';
          btn.click();
          return 'clicked';
        })()
        """;

    private const string HasComposerJs =
        "!!(document.querySelector('[role=\"dialog\"] [contenteditable=\"true\"]') || document.querySelector('[contenteditable=\"true\"]'))";

    private static string InsertTextJs(string text) => $$"""
        (() => {
          const el = document.querySelector('[role="dialog"] [contenteditable="true"]') || document.querySelector('[contenteditable="true"]');
          if (!el) return 'none';
          el.focus();
          document.execCommand('insertText', false, {{JsonSerializer.Serialize(text)}});
          return (el.innerText || '').trim().length > 0 ? 'ok' : 'empty';
        })()
        """;

    /// <summary>
    /// Abre o comentário e devolve, a partir dele, as mensagens da pessoa e as suas.
    /// Também devolve seu usuário detectado na navegação (se não estiver no config.json).
    /// </summary>
    public async Task<(List<ChatMessage> Messages, string? DetectedMe)> ReadAsync(string commentUrl, string handle, string me)
    {
        await page.NavigateAsync(commentUrl);
        if (!await page.WaitForAsync(HasPostLinksJs)) return ([], null);
        await page.ScrollAsync(1.5);
        await Task.Delay(1200);

        var data = await page.EvalJsonAsync<PageData>(ReadJs);
        if (data is null) return ([], null);

        var self = string.IsNullOrWhiteSpace(me) ? data.Me ?? "" : me;
        var start = data.Items.FindIndex(i => i.Url == commentUrl);
        var messages = data.Items
            .Skip(Math.Max(0, start)) // ignora o post original e o que vem acima do comentário
            .Where(i => i.Author.Equals(handle, StringComparison.OrdinalIgnoreCase) ||
                        (self.Length > 0 && i.Author.Equals(self, StringComparison.OrdinalIgnoreCase)))
            .Select(i => new ChatMessage(i.Author, i.Text, i.Author.Equals(self, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return (messages, data.Me);
    }

    /// <summary>Abre o comentário, clica em Responder e digita o texto. Não clica em Publicar.</summary>
    public async Task<FillResult> FillReplyAsync(string commentUrl, string text)
    {
        await page.NavigateAsync(commentUrl);
        if (!await page.WaitForAsync(HasPostLinksJs)) return FillResult.CommentNotFound;

        switch (await page.EvalStringAsync(ClickReplyJs(commentUrl)))
        {
            case "clicked": break;
            case "no_box": return FillResult.CommentNotFound;
            default: return FillResult.ReplyButtonNotFound;
        }

        if (!await page.WaitForAsync(HasComposerJs, ComposerTimeout)) return FillResult.ComposerNotFound;
        await Task.Delay(300);
        return await page.EvalStringAsync(InsertTextJs(text)) == "ok"
            ? FillResult.Filled
            : FillResult.ComposerNotFound;
    }
}
