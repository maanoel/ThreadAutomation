using System.Text.Json;

namespace ThreadOps.Services;

public enum PostResult { Posted, ComposerNotFound, PublishButtonNotFound, NotConfirmed }

/// <summary>Publica no seu perfil um texto que você escreveu, pelo compositor do Threads.</summary>
public sealed class ThreadsPoster(PageDriver page)
{
    private const string BaseUrl = "https://www.threads.com";
    private static readonly TimeSpan ComposerTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(20);

    private const string ComposerJs =
        "(document.querySelector('[role=\"dialog\"] [contenteditable=\"true\"]') || document.querySelector('[contenteditable=\"true\"]'))";

    private const string HasComposerJs = "!!" + ComposerJs;

    // A intenção de post já deveria vir preenchida; se não vier, digita o texto.
    private static string EnsureTextJs(string text) => $$"""
        (() => {
          const el = {{ComposerJs}};
          if (!el) return 'none';
          const want = {{JsonSerializer.Serialize(text)}};
          const norm = s => (s || '').replace(/\s+/g, ' ').trim();
          if (norm(el.innerText) === norm(want)) return 'ok';
          el.focus();
          document.execCommand('selectAll', false, null);
          document.execCommand('insertText', false, want);
          return norm(el.innerText) === norm(want) ? 'ok' : 'mismatch';
        })()
        """;

    private const string ClickPublishJs = """
        (() => {
          const scope = document.querySelector('[role="dialog"]') || document;
          const labels = ['publicar', 'postar', 'post'];
          const btns = [...scope.querySelectorAll('div[role="button"], button')]
            .filter(b => b.offsetParent !== null && labels.includes((b.innerText || '').trim().toLowerCase()));
          const b = btns[btns.length - 1];
          if (!b) return 'not_found';
          if (b.getAttribute('aria-disabled') === 'true' || b.disabled) return 'disabled';
          b.click();
          return 'clicked';
        })()
        """;

    public async Task<PostResult> PostAsync(string text)
    {
        await page.NavigateAsync($"{BaseUrl}/intent/post?text={Uri.EscapeDataString(text)}");
        if (!await page.WaitForAsync(HasComposerJs, ComposerTimeout)) return PostResult.ComposerNotFound;
        await Task.Delay(500);

        if (await page.EvalStringAsync(EnsureTextJs(text)) != "ok") return PostResult.ComposerNotFound;
        await Task.Delay(400);

        if (await page.EvalStringAsync(ClickPublishJs) != "clicked") return PostResult.PublishButtonNotFound;

        // Publicado quando o compositor fecha.
        return await page.WaitForAsync("!" + ComposerJs, ConfirmTimeout) ? PostResult.Posted : PostResult.NotConfirmed;
    }
}
