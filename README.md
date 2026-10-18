# ThreadAutomation · THREAD//OPS

Painel desktop para Windows que **assiste** o uso do [Threads](https://www.threads.com): você vê o Threads ao vivo no centro da tela, e o app ajuda a encontrar perfis, organizar quem seguir, conversar com quem comentou e revisar quem não te segue de volta. As ações são revisadas e confirmadas por você.

> ⚠️ **Aviso.** Automação de interface vai contra os Termos de Uso da Meta, mesmo supervisionada. O uso é por sua conta e risco: a plataforma pode limitar ações ou restringir a conta. Use volumes baixos e mantenha o limite diário ativo.

Um protótipo visual do painel (HTML estático, dados fictícios) está em [`prototipo/painel.html`](prototipo/painel.html).

---

## Funcionalidades

| Área | O que faz |
|---|---|
| **Threads embutido** | O threads.com roda dentro do app (WebView2), com login salvo num perfil próprio do app, separado do seu navegador. |
| **Busca** | Para cada termo (separados por vírgula), abre a busca do Threads, pega os *top N* posts (1–20) e, em cada post, o autor e até 15 pessoas que comentaram. Só lê páginas. |
| **Candidatos** | Lista com o @ de cada perfil encontrado (o texto do post/comentário aparece no tooltip). Filtros *Todos / Autores / Comentaram*, "Marcar visíveis" e "Limpar". |
| **Seguir marcados** | Segue **apenas** os perfis marcados, um por vez, com intervalo fixo de 5 s, conferindo se o Threads confirmou. Para a fila se o Threads recusar a ação ou se 3 perfis seguidos não tiverem botão. Pode ser usado durante a coleta (ela é encerrada antes). |
| **Post antes de seguir** *(opcional)* | Ao confirmar a fila, um popup permite escrever um texto que é publicado no seu perfil antes dos follows. Nada é pré-escrito: o app publica exatamente o que você digitou. |
| **Quem não me segue** | Lê suas listas *Seguindo* e *Seguidores* e lista quem você segue mas não te segue de volta. Quem o app seguiu há menos de **7 dias** fica de fora. Você marca e confirma "Deixar de seguir". |
| **Conversa com IA** | Em um comentarista, o botão 💬 abre a conversa: o app lê o fio, envia ao **seu agente de IA** (endpoint HTTP) e mostra a sugestão num rascunho editável. "Preencher no Threads" só digita a resposta; **quem clica em Publicar é você**. |
| **Controle** | Pausar / Retomar, Parar (mantém o que já foi coletado), radar de status, estatísticas e log ao vivo. |

### Limite diário

- Campo **LIMITE/DIA** (1–50), com contadores separados para *seguir* e *deixar de seguir*.
- A contagem vem dos arquivos de histórico e **zera à meia-noite**.
- O checkbox **APLICAR LIMITE DIÁRIO** vem ligado. Desligá-lo remove o teto (o app registra um aviso no log), o que aumenta bastante o risco de bloqueio pela plataforma.

---

## Requisitos

- Windows 10/11 x64
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (já vem no Windows atualizado)
- Para compilar: [.NET 10 SDK](https://dotnet.microsoft.com/download)

O `.exe` publicado é *self-contained*: não precisa do .NET instalado.

## Como compilar e rodar

```bash
dotnet build ThreadOps.slnx
```

```bash
dotnet run --project src/ThreadOps
```

### Gerar o executável único

```bash
dotnet publish src/ThreadOps/ThreadOps.csproj -c Release -p:PublishSingleFile=true -o publish
```

Resultado: `publish/ThreadOps.exe` (~62 MB, comprimido, win-x64). As opções de publicação ficam no `ThreadOps.csproj`.

## Primeiro uso

1. Abra o `ThreadOps.exe`.
2. Faça login no Threads no painel central. Se usar "Continuar com o Instagram" e cair na home do Instagram, clique em **INÍCIO DO THREADS**.
3. O status fica verde (**PRONTO · sessão do Threads ativa**) quando o login é detectado.

---

## Configuração (`config.json`)

Criado ao lado do executável na primeira execução:

```json
{
  "MeuUsuario": "",
  "Agente": {
    "Url": "",
    "HeaderAutorizacao": "",
    "TimeoutSegundos": 60
  }
}
```

| Campo | Uso |
|---|---|
| `MeuUsuario` | Seu @ (sem `@`). Se vazio, o app detecta e salva sozinho. |
| `Agente.Url` | Endpoint do seu agente de IA (necessário só para a 💬 Conversa). |
| `Agente.HeaderAutorizacao` | Valor opcional do header `Authorization` (ex.: `Bearer xyz`). |
| `Agente.TimeoutSegundos` | Tempo máximo de espera pela resposta do agente. |

Reinicie o app depois de editar.

### Contrato do agente de IA

O app faz `POST` em `Agente.Url` com:

```json
{
  "perfil": "usuario",
  "meuUsuario": "voce",
  "post": { "url": "https://www.threads.com/@autor/post/XYZ", "autor": "autor", "texto": "..." },
  "conversa": [
    { "autor": "usuario", "eu": false, "texto": "comentário da pessoa" },
    { "autor": "voce", "eu": true, "texto": "sua resposta anterior" }
  ]
}
```

E aceita como resposta `{ "resposta": "..." }`, ou os campos `reply`, `text`, `output`, `message`, ou texto puro.

---

## Arquivos gerados (ao lado do `.exe`)

| Arquivo | Conteúdo |
|---|---|
| `config.json` | Configuração acima. |
| `seguidos.log` | Perfis seguidos pelo app (`data hora<TAB>usuario`). Evita repetir perfis e alimenta o limite diário. |
| `deixou-de-seguir.log` | Perfis que o app deixou de seguir, no mesmo formato. |
| `logs/AAAA-MM-DD.log` | Log de execução do dia. |

O login do Threads fica em `%LOCALAPPDATA%\ThreadOps\WebView2`.

---

## Estrutura do projeto

```
ThreadOps.slnx
prototipo/painel.html           Protótipo visual do painel (HTML estático, dados fictícios)
src/ThreadOps/
  App.xaml, Theme.xaml          Aplicação e tema neon
  MainWindow.xaml(.cs)          Painel: controles, Threads embutido, listas, log
  PostDialog.xaml(.cs)          Confirmação da fila + post opcional
  Models/
    Candidate.cs                Perfil listado (busca ou "não me segue")
    ChatMessage.cs              Mensagem da conversa
    LogEntry.cs                 Linha do log
  Services/
    PageDriver.cs               Navegação e execução de scripts no WebView2
    ThreadsScraper.cs           Busca de posts e leitura de comentários
    ThreadsFollower.cs          Seguir um perfil e confirmar
    ThreadsPoster.cs            Publicar um texto no seu perfil
    ThreadsAccount.cs           Seu usuário, listas Seguindo/Seguidores, deixar de seguir
    ThreadsConversation.cs      Ler conversa e preencher resposta
    AgentClient.cs              Chamada ao agente de IA (HTTP)
    AppConfig.cs                config.json
    FollowHistory.cs            seguidos.log / deixou-de-seguir.log
    ActivityLog.cs              Log do painel e em arquivo
```

## Limitações conhecidas

- **Layout do Threads.** O site não tem classes CSS estáveis. Os seletores usam links (`/@usuario`, `/post/`), `data-pressable-container`, `role="tab"` e os textos dos botões (pt-BR e inglês). Uma mudança no site pode quebrar a coleta, o seguir ou a leitura das listas. Nesses casos o log mostra um diagnóstico.
- **Listas grandes.** Ler *Seguindo* e *Seguidores* rola o diálogo até o fim; com mais de mil perfis leva alguns minutos.
- **Uma instância por vez.** Duas janelas abertas disputam o mesmo perfil do WebView2.

## Licença

[MIT](LICENSE)
