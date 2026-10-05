# DashTudo — Blazor Web App (.NET 10)

Reescrita do DashTudo (antes React/TypeScript) em **Blazor Web App + C#**, com login e histórico em **MySQL** e insights por **IA** (Gemini Flash por padrão, trocável pelo `appsettings.json`).

## Funcionalidades

- **Contas**: cadastro, login (cookie, "manter conectado"), bloqueio após 5 tentativas erradas, perfil (editar dados, trocar senha, excluir conta e todos os dados).
- **Upload**: CSV/TSV (detecta `,` `;` tab `|`, UTF-8 ou Windows-1252), Excel `.xlsx`, PDF, TXT/Markdown, ou **colar dados** direto do Excel/Sheets.
- **Detecção automática** de colunas numéricas (inclusive `1.234,56`, `R$ 10,00`, `12%`) e de datas (`dd/MM/yyyy`, ISO...).
- **Gráficos** (Chart.js): barras, barras horizontais, linha, área, pizza e rosca; várias séries; soma/média/contagem/mín/máx; agrupamento de datas por dia/mês/ano; ordenação; "Outros" para categorias excedentes; renomear colunas; exportar PNG; a configuração fica salva.
- **Dados**: tabela paginada e ordenável com filtro (todas as linhas, não só 1000), download do CSV limpo.
- **Estatísticas** por coluna: soma, média, mediana, desvio padrão, mín/máx, vazios, distintos, valores mais frequentes.
- **IA**: resumo executivo, insights e anomalias, relatório completo ou pergunta livre; resposta em streaming; relatórios ficam salvos, podem ser copiados ou baixados em `.md`; limite diário por usuário.
- **Histórico**: busca, filtro por tipo, favoritos.

## Estrutura

```
dashtudo-blazor/
├── DashTudo.slnx / DashTudo.Web.csproj
├── Program.cs              DI, Identity, EF Core, provedores de IA, proxy headers
├── appsettings.json        conexão, limites de upload, configuração da IA (sem segredos)
├── Data/                   entidades, DbContext, migrations, compatibilidade com MariaDB
├── Services/
│   ├── Parsing/            leitura de CSV/Excel/PDF/texto → ParsedDataset
│   ├── Analysis/           estatísticas e agregação dos gráficos
│   ├── Ai/                 AiInsightsService + provedores (Gemini, OpenAI-compatível, Anthropic)
│   └── DatasetService.cs   acesso ao banco (sempre filtrado pelo usuário logado)
├── Components/
│   ├── Account/            login, cadastro, perfil (SSR estático, precisam gravar cookie)
│   ├── Pages/              home, dashboard (upload), dataset, histórico
│   └── Shared/             gráfico, preview, markdown, loading
└── wwwroot/                css, js (interop do Chart.js), imagens, Chart.js local
```

> Como a solução e o projeto estão na mesma pasta, comandos como `build`/`publish` precisam do arquivo: `dotnet build DashTudo.Web.csproj` (o `dotnet run` funciona sem).

Os dados enviados ficam no banco: metadados em `Datasets`, conteúdo normalizado (JSON comprimido com gzip) em `DatasetContents` e relatórios em `AiReports`. PDFs também guardam o arquivo original para a IA ler tabelas/imagens.

## Rodando localmente

Pré-requisitos: [.NET 10 SDK](https://dotnet.microsoft.com/download) e MySQL 8 ou MariaDB 10.6+.

Os segredos de desenvolvimento ficam nos **user-secrets** (fora do repositório, em `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json`), e só são lidos no ambiente `Development`:

```bash
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Port=3306;Database=dashtudo;User=dashtudo;Password=SUA_SENHA;"
dotnet user-secrets set "Ai:Gemini:ApiKey" "SUA_CHAVE"     # opcional, para a IA
dotnet user-secrets list
dotnet run
```

As tabelas são criadas automaticamente na primeira execução (`Database:MigrateOnStartup`). Para criar novas migrations depois de mudar as entidades:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add NomeDaMudanca -o Data/Migrations
```

## Configurando a IA

Tudo fica na seção `Ai` do `appsettings.json` (ou em variáveis de ambiente como `Ai__Provider`):

| `Ai:Provider` | O que é | Configuração |
|---|---|---|
| `Gemini` (padrão) | Google Gemini. `gemini-3.8-flash` tem camada gratuita | `Ai:Gemini:ApiKey` (crie em https://aistudio.google.com/apikey), `Ai:Gemini:Model` |
| `OpenAICompatible` | Qualquer API `/chat/completions`: Groq, OpenRouter, DeepSeek, Ollama local... | `BaseUrl`, `Model`, `ApiKey` |
| `Anthropic` | Claude (pago) | `Ai:Anthropic:ApiKey` (ou `ANTHROPIC_API_KEY`), `Model`, `Effort` |

Outras opções: `MaxOutputTokens`, `MaxDataChars` (acima disso a IA recebe uma amostra das linhas e o usuário é avisado; as estatísticas continuam calculadas sobre tudo) e `DailyLimitPerUser`.

> ⚠️ **Privacidade**: na camada gratuita do Gemini, o Google pode usar o conteúdo enviado para melhorar os produtos dele. Se seus usuários forem subir dados sensíveis, avise nos termos de uso ou use a camada paga/outro provedor.

Para adicionar outro provedor: implemente `IAiProvider` (veja `GeminiProvider.cs`) e registre no `Program.cs`.

## Deploy na VPS (Debian + MariaDB + Nginx)

O app roda como serviço `systemd` em `127.0.0.1:5080`, e o Nginx faz o proxy com HTTPS. Em produção os segredos ficam em `/etc/dashtudo/dashtudo.env` (o equivalente aos user-secrets).

**Publicar e enviar** (na sua máquina):

```bash
dotnet publish DashTudo.Web.csproj -c Release -o bin/publish
rsync -avz --delete bin/publish/ usuario@vps:/var/www/dashtudo-blazor/
ssh usuario@vps 'sudo systemctl restart dashtudo'
```

**`/etc/dashtudo/dashtudo.env`** (`chmod 600`):

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5080
ConnectionStrings__Default=Server=localhost;Port=3306;Database=dashtudo;User=dashtudo;Password=SENHA_DA_VPS;
DataProtection__KeysPath=/var/lib/dashtudo/keys
Ai__Provider=Gemini
Ai__Gemini__ApiKey=SUA_CHAVE
```

**`/etc/systemd/system/dashtudo.service`**:

```ini
[Unit]
Description=DashTudo (Blazor)
After=network.target mariadb.service

[Service]
WorkingDirectory=/var/www/dashtudo-blazor
ExecStart=/usr/bin/dotnet /var/www/dashtudo-blazor/DashTudo.Web.dll
EnvironmentFile=/etc/dashtudo/dashtudo.env
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=dashtudo
User=dashtudo
Group=dashtudo

[Install]
WantedBy=multi-user.target
```

**`/etc/nginx/sites-available/dashtudo`** — o essencial é repassar WebSocket (o Blazor Server depende dele):

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }

server {
    listen 80;
    server_name seu-dominio.com.br;
    client_max_body_size 30m;

    location / {
        proxy_pass         http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_read_timeout 300s;
    }
}
```

> **MariaDB**: suportado e testado (10.11). O provedor oficial `MySql.EntityFrameworkCore` quebra no lock de migrations do MariaDB (`GET_LOCK(..., -1)` retorna NULL); o projeto contorna isso com `Data/MariaDbCompatibleHistoryRepository.cs`. Ao atualizar o pacote do provedor, rode o app uma vez num banco de teste para confirmar.

### Dicas para a VPS

- **Não exponha o banco**: `bind-address = 127.0.0.1` e firewall (`ufw allow OpenSSH && ufw allow 'Nginx Full' && ufw enable`).
- **Backup diário**: `mysqldump --single-transaction dashtudo | gzip > /var/backups/dashtudo-$(date +%F).sql.gz` no cron do root.
- **Memória**: cada aba aberta num dataset mantém as linhas na memória do servidor. Numa VPS de 1–2 GB, mantenha `Upload:MaxRows` em 200 mil ou menos.
- **Logs**: `journalctl -u dashtudo -f`.

## Sugestões de próximos passos

- Confirmação de email e "esqueci minha senha" (o Identity já suporta; falta configurar um serviço de envio, ex. SMTP do provedor do domínio).
- Vários gráficos salvos por dataset (montar um "painel").
- Chat com a IA mantendo o contexto da conversa.
- Exportar relatório em PDF (hoje dá para imprimir em PDF pelo navegador — o CSS já esconde menus na impressão).
- Escolher a aba do Excel quando houver várias (hoje importa a primeira com dados).
- Compartilhar um dataset/relatório por link público somente leitura.
- Testes automatizados para os parsers (`ValueParser`, CSV pt-BR) com xUnit.
