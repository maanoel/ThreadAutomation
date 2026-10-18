namespace ThreadOps.Services;

public sealed record PostInfo(string Url, string Author, string Text);
public sealed record ReplyInfo(string Author, string Text, string? Url, int? AgeMinutes = null);

/// <summary>
/// Lê dados das páginas do Threads abertas no WebView2. Só lê o DOM: não clica, não curte, não segue.
/// O Threads não tem classes CSS estáveis, então os seletores usam links (/@usuario, /post/) e
/// o atributo data-pressable-container, que marca cada post/resposta.
/// </summary>
public sealed class ThreadsScraper(PageDriver page)
{
    private const string BaseUrl = "https://www.threads.com";

    // Compartilhado com ThreadsConversation.
    internal const string HelpersJs = """
        const __longest = (box, handle) => {
          if (!box) return '';
          let best = '';
          for (const s of box.querySelectorAll('span[dir="auto"]')) {
            const t = (s.innerText || '').trim();
            if (!t || t === handle) continue;
            if (/^\d+\s*(s|m|min|h|d|sem)$/i.test(t) || /^[\d.,]+\s*(mil|k|m)?$/i.test(t)) continue;
            if (t.length > best.length) best = t;
          }
          return best.replace(/\s+/g, ' ').slice(0, 280);
        };
        const __author = box => {
          for (const a of box.querySelectorAll('a[href^="/@"]')) {
            const m = a.getAttribute('href').match(/^\/@([^/?#]+)\/?$/);
            if (m) return m[1];
          }
          const p = box.querySelector('a[href*="/post/"]');
          const m = p && p.getAttribute('href').match(/^\/@([^/]+)\/post\//);
          return m ? m[1] : null;
        };
        const __url = box => {
          const p = box.querySelector('a[href*="/post/"]');
          const m = p && p.getAttribute('href').match(/^\/@([^/]+)\/post\/([^/?#]+)/);
          return m ? 'https://www.threads.com/@' + m[1] + '/post/' + m[2] : null;
        };
        // Idade do comentário em minutos (menor = mais recente). Usa <time datetime> se houver,
        // senão o rótulo relativo ("5m", "2h", "3d", "1sem"). Retorna null se não achar.
        const __age = box => {
          if (!box) return null;
          const t = box.querySelector('time[datetime]');
          if (t) {
            const ms = Date.now() - Date.parse(t.getAttribute('datetime'));
            if (!isNaN(ms)) return Math.max(0, Math.round(ms / 60000));
          }
          const units = { s: 0, min: 1, m: 1, h: 60, d: 1440, sem: 10080 };
          let best = null;
          for (const el of box.querySelectorAll('span, time, a')) {
            const m = (el.innerText || '').trim().match(/^(\d+)\s*(s|min|m|h|d|sem)$/i);
            if (!m) continue;
            const mins = Math.round(parseInt(m[1], 10) * units[m[2].toLowerCase()]);
            if (best === null || mins < best) best = mins;
          }
          return best;
        };
        """;

    // Os helpers ficam dentro de cada IIFE: no escopo global, "const" falharia na segunda execução.
    private static readonly string SearchPostsJs = "(() => {" + HelpersJs + """
          const out = [], seen = new Set();
          for (const a of document.querySelectorAll('a[href*="/post/"]')) {
            const m = a.getAttribute('href').match(/^\/@([^/]+)\/post\/([^/?#]+)/);
            if (!m) continue;
            const key = m[1] + '/' + m[2];
            if (seen.has(key)) continue;
            seen.add(key);
            const box = a.closest('[data-pressable-container="true"]') || a.parentElement;
            out.push({ url: 'https://www.threads.com/@' + key.replace('/', '/post/'), author: m[1], text: __longest(box, m[1]) });
          }
          return JSON.stringify(out);
        })()
        """;

    private static readonly string PostPageJs = "(() => {" + HelpersJs + """
          const out = [];
          for (const box of document.querySelectorAll('[data-pressable-container="true"]')) {
            const h = __author(box);
            if (h) out.push({ author: h, text: __longest(box, h), url: __url(box), ageMinutes: __age(box) });
          }
          return JSON.stringify(out);
        })()
        """;

    private const string HasPostLinksJs = "document.querySelectorAll('a[href*=\"/post/\"]').length > 0";

    private const string DiagnosticsJs = """
        JSON.stringify({
          url: location.pathname,
          postLinks: document.querySelectorAll('a[href*="/post/"]').length,
          containers: document.querySelectorAll('[data-pressable-container="true"]').length,
          profileLinks: document.querySelectorAll('a[href^="/@"]').length
        })
        """;

    /// <summary>Abre a busca (aba Principais) e devolve os primeiros posts, rolando a página se faltar.</summary>
    public async Task<List<PostInfo>> SearchTopPostsAsync(string term, int count, Func<Task> checkpoint)
    {
        await page.NavigateAsync($"{BaseUrl}/search?q={Uri.EscapeDataString(term)}&serp_type=default");
        if (!await page.WaitForAsync(HasPostLinksJs)) return [];

        // O Threads tira do DOM os posts que saem da tela, então acumulamos entre as rodadas.
        var posts = new List<PostInfo>();
        var seen = new HashSet<string>();
        for (var round = 0; round < 10 && posts.Count < count; round++)
        {
            await checkpoint();
            foreach (var p in await page.EvalJsonAsync<List<PostInfo>>(SearchPostsJs) ?? [])
                if (seen.Add(p.Url)) posts.Add(p);
            if (posts.Count >= count) break;
            await page.ScrollAsync(1.5);
            await Task.Delay(1500);
        }
        return posts.Take(count).ToList();
    }

    /// <summary>Abre o post e devolve quem respondeu (sem o autor), com o texto da resposta.</summary>
    public async Task<List<ReplyInfo>> ReadRepliesAsync(PostInfo post, int max)
    {
        await page.NavigateAsync(post.Url);
        if (!await page.WaitForAsync(HasPostLinksJs)) return [];

        // Uma rolada para carregar as primeiras respostas.
        await page.ScrollAsync(1.5);
        await Task.Delay(1200);

        var items = await page.EvalJsonAsync<List<ReplyInfo>>(PostPageJs) ?? [];
        return items
            .Skip(1) // o primeiro container é o próprio post
            .Where(r => !r.Author.Equals(post.Author, StringComparison.OrdinalIgnoreCase))
            // Mais recentes primeiro: quem comentou há pouco tende a estar online e seguir de volta.
            // OrderBy é estável, então sem data a ordem da página é mantida.
            .OrderBy(r => r.AgeMinutes ?? int.MaxValue)
            .Take(max)
            .ToList();
    }

    /// <summary>Contagens do DOM atual, para o log quando a coleta não encontra nada.</summary>
    public async Task<string> DiagnosticsAsync() =>
        await page.EvalStringAsync(DiagnosticsJs) ?? "(sem dados)";
}
