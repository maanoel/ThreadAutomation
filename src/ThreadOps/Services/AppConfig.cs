using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThreadOps.Services;

/// <summary>config.json ao lado do executável. Criado com valores vazios na primeira execução.</summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>Seu usuário no Threads (sem @). Usado para separar suas mensagens das da outra pessoa.</summary>
    public string MeuUsuario { get; set; } = "";

    public AgentSettings Agente { get; set; } = new();

    public sealed class AgentSettings
    {
        /// <summary>URL que recebe POST com a conversa e devolve a resposta sugerida.</summary>
        public string Url { get; set; } = "";

        /// <summary>Valor opcional do header Authorization (ex.: "Bearer xyz").</summary>
        public string HeaderAutorizacao { get; set; } = "";

        public int TimeoutSegundos { get; set; } = 60;
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));

    public static AppConfig Load()
    {
        if (File.Exists(FilePath))
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json) ?? new AppConfig();

        var config = new AppConfig();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Json));
        return config;
    }
}
