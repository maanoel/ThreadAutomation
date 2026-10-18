using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ThreadOps.Services;

/// <summary>Navegação e execução de scripts no WebView2, compartilhadas pelo leitor e pelo seguidor.</summary>
public sealed class PageDriver(CoreWebView2 web)
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Navega e espera carregar. Tenta de novo uma vez: uma navegação ainda em andamento
    /// (redirecionamento do próprio Threads) pode abortar a nova com ConnectionAborted.
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        if (await TryNavigateAsync(url)) return;
        await Task.Delay(1500);
        if (!await TryNavigateAsync(url))
            throw new InvalidOperationException($"Falha ao carregar {url}");
    }

    private async Task<bool> TryNavigateAsync(string url)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            web.NavigationCompleted -= Handler;
            done.TrySetResult(e.IsSuccess);
        }
        web.NavigationCompleted += Handler;
        web.Navigate(url);

        try
        {
            return await done.Task.WaitAsync(LoadTimeout);
        }
        catch (TimeoutException)
        {
            web.NavigationCompleted -= Handler;
            return false;
        }
    }

    /// <summary>Repete o script (que deve retornar true/false) até ser true ou estourar o tempo.</summary>
    public async Task<bool> WaitForAsync(string conditionJs, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? LoadTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await web.ExecuteScriptAsync(conditionJs) == "true") return true;
            await Task.Delay(500);
        }
        return false;
    }

    public Task ScrollAsync(double screens) =>
        web.ExecuteScriptAsync($"window.scrollBy(0, window.innerHeight * {screens.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

    /// <summary>Executa um script que retorna uma string e devolve essa string.</summary>
    public async Task<string?> EvalStringAsync(string script) =>
        JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync(script));

    /// <summary>
    /// Executa um script que retorna JSON.stringify(...). O ExecuteScriptAsync serializa o resultado
    /// de novo, então a string precisa ser desserializada duas vezes.
    /// </summary>
    public async Task<T?> EvalJsonAsync<T>(string script)
    {
        var inner = await EvalStringAsync(script);
        return inner is null ? default : JsonSerializer.Deserialize<T>(inner, Json);
    }
}
