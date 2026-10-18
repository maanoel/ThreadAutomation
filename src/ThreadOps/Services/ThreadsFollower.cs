namespace ThreadOps.Services;

public enum FollowResult
{
    /// <summary>Clicou em Seguir e o botão mudou para Seguindo/Solicitado.</summary>
    Followed,
    /// <summary>Você já seguia o perfil (ou já havia solicitado).</summary>
    AlreadyFollowing,
    /// <summary>O botão Seguir não apareceu (perfil inexistente, layout mudou etc.).</summary>
    ButtonNotFound,
    /// <summary>Clicou, mas o Threads não confirmou — pode ser limite de ação da plataforma.</summary>
    NotConfirmed,
}

/// <summary>Segue um único perfil pelo botão da página de perfil e confere se o Threads aceitou.</summary>
public sealed class ThreadsFollower(PageDriver page)
{
    private const string BaseUrl = "https://www.threads.com";
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(6);

    private const string LabelsJs = """
        const __norm = t => (t || '').trim().toLowerCase();
        const __FOLLOW = ['seguir', 'follow', 'seguir de volta', 'follow back'];
        const __DONE = ['seguindo', 'following', 'solicitado', 'requested'];
        const __buttons = () => [...document.querySelectorAll('div[role="button"], button')]
          .filter(b => b.offsetParent !== null);
        """;

    // O primeiro botão Seguir/Seguindo da página é o do cabeçalho do perfil.
    private static readonly string HasFollowButtonJs = "(() => {" + LabelsJs + """
          return __buttons().some(b => __FOLLOW.includes(__norm(b.innerText)) || __DONE.includes(__norm(b.innerText)));
        })()
        """;

    private static readonly string ClickFollowJs = "(() => {" + LabelsJs + """
          const b = __buttons().find(b => __FOLLOW.includes(__norm(b.innerText)) || __DONE.includes(__norm(b.innerText)));
          if (!b) return 'not_found';
          if (__DONE.includes(__norm(b.innerText))) return 'already';
          b.setAttribute('data-threadops-follow', '1');
          b.click();
          return 'clicked';
        })()
        """;

    private static readonly string IsConfirmedJs = "(() => {" + LabelsJs + """
          const b = document.querySelector('[data-threadops-follow="1"]');
          return !!b && __DONE.includes(__norm(b.innerText));
        })()
        """;

    public async Task<FollowResult> FollowAsync(string handle)
    {
        await page.NavigateAsync($"{BaseUrl}/@{Uri.EscapeDataString(handle)}");
        if (!await page.WaitForAsync(HasFollowButtonJs)) return FollowResult.ButtonNotFound;

        switch (await page.EvalStringAsync(ClickFollowJs))
        {
            case "already": return FollowResult.AlreadyFollowing;
            case "clicked": break;
            default: return FollowResult.ButtonNotFound;
        }

        return await page.WaitForAsync(IsConfirmedJs, ConfirmTimeout)
            ? FollowResult.Followed
            : FollowResult.NotConfirmed;
    }
}
