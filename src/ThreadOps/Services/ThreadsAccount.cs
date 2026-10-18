namespace ThreadOps.Services;

public enum FollowListKind { Followers, Following }

public enum UnfollowResult
{
    /// <summary>Clicou em Seguindo → Deixar de seguir e o botão voltou para Seguir.</summary>
    Unfollowed,
    /// <summary>Você já não seguia o perfil.</summary>
    NotFollowing,
    /// <summary>Botão Seguindo ou a confirmação não apareceram.</summary>
    ButtonNotFound,
    /// <summary>Clicou, mas o Threads não confirmou — pode ser limite de ação da plataforma.</summary>
    NotConfirmed,
}

/// <summary>Sua conta: detecta seu usuário, lê Seguidores/Seguindo e deixa de seguir um perfil.</summary>
public sealed class ThreadsAccount(PageDriver page)
{
    private const string BaseUrl = "https://www.threads.com";
    private const int MaxIdleRounds = 6;
    private const int MaxRounds = 400;

    private const string DetectMeJs = """
        (() => {
          const a = document.querySelector('nav a[href^="/@"], [role="navigation"] a[href^="/@"]');
          const m = a && a.getAttribute('href').match(/^\/@([^/?#]+)/);
          return m ? m[1] : '';
        })()
        """;

    // O contador "N seguidores" do cabeçalho do perfil abre o diálogo com as abas Seguidores/Seguindo.
    private const string OpenFollowersDialogJs = """
        (() => {
          const els = [...document.querySelectorAll('a, span, div[role="button"]')]
            .filter(e => e.offsetParent !== null && /\b(seguidor(es)?|followers?)\b/i.test(e.innerText || '') && (e.innerText || '').length < 40);
          const el = els[0];
          if (!el) return 'not_found';
          (el.closest('a, [role="button"]') || el).click();
          return 'clicked';
        })()
        """;

    private const string HasFollowersCounterJs = """
        [...document.querySelectorAll('a, span, div[role="button"]')]
          .some(e => e.offsetParent !== null && /\b(seguidor(es)?|followers?)\b/i.test(e.innerText || '') && (e.innerText || '').length < 40)
        """;

    // A página pode ter outros [role=dialog] (vazios/ocultos); o certo é o que mostra Seguidores/Seguindo.
    private const string FindDialogJs =
        "[...document.querySelectorAll('[role=\"dialog\"]')].find(d => /(seguidores|followers)/i.test(d.innerText || '') && /(seguindo|following)/i.test(d.innerText || ''))";

    private const string HasDialogJs = "!!" + FindDialogJs;

    private static string SelectTabJs(FollowListKind kind) => $$"""
        (() => {
          const d = {{FindDialogJs}};
          if (!d) return 'no_dialog';
          const re = {{(kind == FollowListKind.Followers ? "/^(seguidores|followers)/i" : "/^(seguindo|following)/i")}};
          // Estrutura real: div[role=tab] "Seguindo 1.249" > div[role=button]. As linhas da lista
          // também têm botões "Seguindo", por isso a busca é só entre os [role=tab].
          const tab = [...d.querySelectorAll('[role="tab"]')].find(e => re.test((e.innerText || '').trim()));
          if (!tab) {
            // Diagnóstico: textos curtos visíveis no diálogo, para ajustar o seletor.
            const seen = [...new Set([...d.querySelectorAll('*')]
              .filter(e => e.children.length === 0 && e.offsetParent !== null)
              .map(e => (e.innerText || '').trim()).filter(t => t && t.length < 30))].slice(0, 15);
            return 'no_tab: ' + seen.join(' | ');
          }
          if (tab.getAttribute('aria-selected') === 'true') return 'clicked';
          const target = tab.querySelector('[role="button"]') || tab;
          const r = target.getBoundingClientRect();
          const opts = { bubbles: true, cancelable: true, view: window, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 };
          for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup'])
            target.dispatchEvent(new (type.startsWith('pointer') ? PointerEvent : MouseEvent)(type, opts));
          target.click();
          return 'clicked';
        })()
        """;

    private static string TabSelectedJs(FollowListKind kind) => $$"""
        (() => {
          const d = {{FindDialogJs}};
          const re = {{(kind == FollowListKind.Followers ? "/^(seguidores|followers)/i" : "/^(seguindo|following)/i")}};
          const tab = d && [...d.querySelectorAll('[role="tab"]')].find(e => re.test((e.innerText || '').trim()));
          return !!tab && tab.getAttribute('aria-selected') === 'true';
        })()
        """;

    // Diagnóstico da área acima da lista: textos curtos com tag/role e posição.
    private const string HeaderDiagnosticsJs = """
        (() => {
          const d =
        """ + FindDialogJs + """
        ;
          if (!d) return 'sem diálogo';
          const firstLink = d.querySelector('a[href^="/@"]');
          const listTop = firstLink ? firstLink.getBoundingClientRect().top : Infinity;
          return [...d.querySelectorAll('*')]
            .filter(e => e.offsetParent !== null && e.getBoundingClientRect().bottom <= listTop)
            .filter(e => { const t = (e.innerText || '').trim(); return t && t.length < 40; })
            .slice(0, 25)
            .map(e => `${e.tagName.toLowerCase()}${e.getAttribute('role') ? '[' + e.getAttribute('role') + ']' : ''}` +
                      `${e.getAttribute('aria-selected') ? '{sel=' + e.getAttribute('aria-selected') + '}' : ''}` +
                      `"${(e.innerText || '').trim().replace(/\s+/g, ' ')}"@${Math.round(e.getBoundingClientRect().left)},${Math.round(e.getBoundingClientRect().top)}`)
            .join(' | ');
        })()
        """;

    private const string FirstHandlesJs = """
        (() => {
          const d =
        """ + FindDialogJs + """
        ;
          if (!d) return '';
          return [...d.querySelectorAll('a[href^="/@"]')].slice(0, 8).map(a => a.getAttribute('href')).join(',');
        })()
        """;

    private const string CollectAndScrollJs = """
        (() => {
          const d =
        """ + FindDialogJs + """
        ;
          if (!d) return JSON.stringify(null);
          const handles = [...new Set([...d.querySelectorAll('a[href^="/@"]')]
            .map(a => (a.getAttribute('href').match(/^\/@([^/?#]+)\/?$/) || [])[1])
            .filter(Boolean))];
          // Levar o último perfil até a vista dispara o carregamento da próxima página,
          // seja qual for o elemento que rola.
          const links = d.querySelectorAll('a[href^="/@"]');
          if (links.length) links[links.length - 1].scrollIntoView({ block: 'end' });
          const scroller = [...d.querySelectorAll('*')].find(e => e.scrollHeight > e.clientHeight + 20 &&
            /(auto|scroll)/.test(getComputedStyle(e).overflowY));
          if (scroller) scroller.scrollTop = scroller.scrollHeight;
          return JSON.stringify(handles);
        })()
        """;

    private const string LabelsJs = """
        const __norm = t => (t || '').trim().toLowerCase();
        const __FOLLOW = ['seguir', 'follow', 'seguir de volta', 'follow back'];
        const __DONE = ['seguindo', 'following', 'solicitado', 'requested'];
        const __buttons = (root) => [...(root || document).querySelectorAll('div[role="button"], button')]
          .filter(b => b.offsetParent !== null);
        """;

    private static readonly string HasProfileButtonJs = "(() => {" + LabelsJs + """
          return __buttons().some(b => __FOLLOW.includes(__norm(b.innerText)) || __DONE.includes(__norm(b.innerText)));
        })()
        """;

    private static readonly string ClickFollowingJs = "(() => {" + LabelsJs + """
          const b = __buttons().find(b => __FOLLOW.includes(__norm(b.innerText)) || __DONE.includes(__norm(b.innerText)));
          if (!b) return 'not_found';
          if (!__DONE.includes(__norm(b.innerText))) return 'not_following';
          b.setAttribute('data-threadops-unfollow', '1');
          b.click();
          return 'clicked';
        })()
        """;

    private static readonly string ConfirmUnfollowJs = "(() => {" + LabelsJs + """
          const scope = document.querySelector('[role="dialog"]') || document;
          const b = __buttons(scope).find(b => ['deixar de seguir', 'unfollow'].includes(__norm(b.innerText)));
          if (!b) return false;
          b.click();
          return true;
        })()
        """;

    private static readonly string IsUnfollowedJs = "(() => {" + LabelsJs + """
          const b = document.querySelector('[data-threadops-unfollow="1"]');
          return !!b && __FOLLOW.includes(__norm(b.innerText));
        })()
        """;

    public async Task<string> DetectMeAsync()
    {
        await page.NavigateAsync(BaseUrl + "/");
        await page.WaitForAsync("!!document.querySelector('nav a[href^=\"/@\"], [role=\"navigation\"] a[href^=\"/@\"]')");
        return await page.EvalStringAsync(DetectMeJs) ?? "";
    }

    /// <summary>
    /// Abre seu perfil, o diálogo de seguidores e a aba pedida, e rola até não aparecer ninguém novo.
    /// <paramref name="progress"/> recebe o total lido até o momento.
    /// </summary>
    public async Task<HashSet<string>> ReadListAsync(string me, FollowListKind kind, Func<Task> checkpoint, Action<int> progress)
    {
        await page.NavigateAsync($"{BaseUrl}/@{Uri.EscapeDataString(me)}");
        // No seu perfil não há botão Seguir; espera o contador de seguidores aparecer.
        await page.WaitForAsync(HasFollowersCounterJs);
        await Task.Delay(800);

        if (await page.EvalStringAsync(OpenFollowersDialogJs) != "clicked")
            throw new InvalidOperationException("Contador de seguidores não encontrado no seu perfil.");
        if (!await page.WaitForAsync(HasDialogJs))
            throw new InvalidOperationException("A lista de seguidores não abriu.");
        await Task.Delay(800);

        // O diálogo abre em Seguidores. Ao ir para Seguindo, os primeiros perfis têm que mudar;
        // se não mudarem, a aba não trocou e o app leria a lista errada.
        var firstBefore = await page.EvalStringAsync(FirstHandlesJs);
        var tab = await page.EvalStringAsync(SelectTabJs(kind));
        if (tab != "clicked")
            throw new InvalidOperationException($"Aba {(kind == FollowListKind.Followers ? "Seguidores" : "Seguindo")} não encontrada ({tab}).");
        // A aba certa fica com aria-selected=true; depois a lista precisa ter mudado.
        await page.WaitForAsync(TabSelectedJs(kind), TimeSpan.FromSeconds(6));
        await Task.Delay(1500);
        if (kind == FollowListKind.Following && await page.EvalStringAsync(FirstHandlesJs) == firstBefore)
            throw new InvalidOperationException("A aba Seguindo não abriu (a lista não mudou). Nada foi lido para não gerar resultado errado. " +
                "Cabeçalho: " + await page.EvalStringAsync(HeaderDiagnosticsJs));

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idle = 0;
        for (var round = 0; round < MaxRounds && idle < MaxIdleRounds; round++)
        {
            await checkpoint();
            var batch = await page.EvalJsonAsync<List<string>>(CollectAndScrollJs) ?? [];
            var before = all.Count;
            foreach (var h in batch)
                if (!h.Equals(me, StringComparison.OrdinalIgnoreCase)) all.Add(h);
            idle = all.Count == before ? idle + 1 : 0;
            progress(all.Count);
            await Task.Delay(1500);
        }
        return all;
    }

    public async Task<UnfollowResult> UnfollowAsync(string handle)
    {
        await page.NavigateAsync($"{BaseUrl}/@{Uri.EscapeDataString(handle)}");
        if (!await page.WaitForAsync(HasProfileButtonJs)) return UnfollowResult.ButtonNotFound;

        switch (await page.EvalStringAsync(ClickFollowingJs))
        {
            case "not_following": return UnfollowResult.NotFollowing;
            case "clicked": break;
            default: return UnfollowResult.ButtonNotFound;
        }

        if (!await page.WaitForAsync(ConfirmUnfollowJs, TimeSpan.FromSeconds(5))) return UnfollowResult.ButtonNotFound;
        return await page.WaitForAsync(IsUnfollowedJs, TimeSpan.FromSeconds(6))
            ? UnfollowResult.Unfollowed
            : UnfollowResult.NotConfirmed;
    }
}
