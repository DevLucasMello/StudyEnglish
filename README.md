# StudyEnglish – Daily English Email (com áudio) via GitHub Actions

Este projeto automatiza o envio de **um e-mail diário para estudo de inglês**, gerando:

- **Body em HTML** com conteúdo de estudo (palavras / expressões / frases)
- **Áudio (TTS)** para treinar listening (geralmente ~9 minutos, dependendo do conteúdo)
- Persistência de estado/cache para **evitar repetição**, mesmo rodando em ambiente “descartável” (GitHub Actions)

✅ Roda 1 vez por dia, no horário que você definir, **sem depender do seu PC ligado**.

Repositório (open source): https://github.com/DevLucasMello/StudyEnglish

---

## O que foi implementado

### 1) Agendamento diário (scheduler)
- O agendamento roda via **GitHub Actions** usando `schedule` (cron).
- O cron do GitHub Actions é em **UTC** (atenção ao fuso do Brasil).

### 2) Execução em Windows runner (importante para o áudio)
- O áudio usa **Windows Text-to-Speech** (`System.Speech.Synthesis`).
- Por isso o workflow roda em `windows-latest`.

### 3) Integrações
Dependendo do seu `Program.cs`:
- **SMTP** para envio do e-mail
- **API de geração de conteúdo (LLM)** compatível com OpenAI (chave em `OPENAI_API_KEY`)
- **DeepL** (opcional) para tradução/cache (`DEEPL_AUTH_KEY`)

### 4) Persistência (estado/cache) sem servidor
GitHub Actions roda em máquinas temporárias, então nada fica salvo entre execuções.  
Para não “perder memória” do que já foi enviado, o projeto:

- Mantém arquivos de estado/cache no repositório
- Faz **commit automático** desses arquivos ao final do job

Arquivos persistidos:
- `sent_state.json` → controle do que já foi enviado
- `deepl_sentence_cache.json` → cache de frases/traduções (se aplicável)
- `blocked_words.log` → log de bloqueios/erros tratados

---

## Estrutura do repositório

Recomendação (exemplo realista):

```text
.
├─ EnvioEmailsEnglish/
│  ├─ EnvioEmailsEnglish.csproj
│  └─ Program.cs
├─ .github/
│  └─ workflows/
│     └─ daily-email.yml
├─ english-vocabulary.txt
├─ sent_state.json
├─ deepl_sentence_cache.json
├─ blocked_words.log
└─ README.md
```

📌 O GitHub Actions só reconhece workflows em:  
`.github/workflows/*.yml` (ou `.yaml`)

---

## Como configurar (passo a passo)

### 1) Arquivos na raiz
Na raiz do repo, tenha:

- `english-vocabulary.txt`
- `sent_state.json`
- `deepl_sentence_cache.json`
- `blocked_words.log`

Sugestão de conteúdo inicial:

**sent_state.json**
```json
{
  "sent": [],
  "blocked": {},
  "current": null
}
```

**deepl_sentence_cache.json**
```json
{}
```

**blocked_words.log**
```text

```

---

### 2) Criar Secrets no GitHub (obrigatório)
Repo → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**

Crie os secrets (nomes sugeridos):

Obrigatórios:
- `OPENAI_API_KEY` (chave da API que você usa para gerar conteúdo)
- `SMTP_HOST`
- `SMTP_PORT`
- `SMTP_USER`
- `SMTP_PASS`
- `EMAIL_TO`

Opcional:
- `DEEPL_AUTH_KEY`

⚠️ Não coloque chaves em arquivos do repositório (principalmente por ser público). Use apenas **Secrets**.

---

### 3) Permitir que o workflow faça commit/push
Repo → **Settings** → **Actions** → **General** → **Workflow permissions**

- Marque **Read and write permissions**

Isso permite que o job faça commit automático dos JSONs/log.

---

### 4) Workflow do GitHub Actions
Crie/edite o arquivo:

`.github/workflows/daily-email.yml`

Exemplo completo (ajuste o path do `.csproj` se o seu for diferente):

```yml
name: Daily English Email

on:
  schedule:
    - cron: "0 11 * * *"  # 08:00 no Brasil (UTC-3)
  workflow_dispatch:

permissions:
  contents: write

concurrency:
  group: daily-english-email
  cancel-in-progress: false

jobs:
  run:
    runs-on: windows-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "8.0.x"

      - name: Restore
        run: dotnet restore ./EnvioEmailsEnglish/EnvioEmailsEnglish.csproj

      - name: Run app
        env:
          OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
          DEEPL_AUTH_KEY: ${{ secrets.DEEPL_AUTH_KEY }}

          SMTP_HOST: ${{ secrets.SMTP_HOST }}
          SMTP_PORT: ${{ secrets.SMTP_PORT }}
          SMTP_USER: ${{ secrets.SMTP_USER }}
          SMTP_PASS: ${{ secrets.SMTP_PASS }}
          EMAIL_TO: ${{ secrets.EMAIL_TO }}

          # arquivos do repo (persistência via commit)
          VOCABULARY_PATH: "english-vocabulary.txt"
          STATE_PATH: "sent_state.json"
          DEEPL_CACHE_PATH: "deepl_sentence_cache.json"
          BLOCKED_LOG_PATH: "blocked_words.log"

          # áudio (TTS)
          EMAIL_AUDIO_ENABLED: "true"
          EMAIL_AUDIO_DIR: "audio"
          EMAIL_AUDIO_VOICE_CULTURE: "en-US"

        run: dotnet run -c Release --project ./EnvioEmailsEnglish/EnvioEmailsEnglish.csproj

      - name: Commit updated state/cache
        run: |
          git config user.name "github-actions[bot]"
          git config user.email "github-actions[bot]@users.noreply.github.com"

          git add sent_state.json deepl_sentence_cache.json blocked_words.log
          git commit -m "chore: update daily state/cache" || echo "No changes"
          git push
```

---

## Agendamento (cron) e fuso horário

O cron do GitHub Actions é **UTC**.

Brasil (UTC-3), exemplos comuns:
- 07:00 Brasil → `0 10 * * *`
- 08:00 Brasil → `0 11 * * *`
- 09:00 Brasil → `0 12 * * *`

Basta ajustar o `cron:` e commitar.

---

## Como testar manualmente (agora)

1. Vá em **Actions**
2. Abra o workflow **Daily English Email**
3. Clique em **Run workflow**
4. Acompanhe os logs do job

Você deve ver:
- job executando com sucesso
- e-mail chegando no destino
- commit automático atualizando `sent_state.json`, `deepl_sentence_cache.json`, `blocked_words.log`

---

## Variáveis de ambiente usadas pelo app

Estas variáveis são passadas pelo workflow:

- `VOCABULARY_PATH` → caminho do arquivo `english-vocabulary.txt`
- `STATE_PATH` → onde salvar o estado (ex.: `sent_state.json`)
- `DEEPL_CACHE_PATH` → cache (ex.: `deepl_sentence_cache.json`)
- `BLOCKED_LOG_PATH` → log (ex.: `blocked_words.log`)

Áudio:
- `EMAIL_AUDIO_ENABLED` → `true|false`
- `EMAIL_AUDIO_DIR` → diretório do áudio (ex.: `audio`)
- `EMAIL_AUDIO_VOICE_CULTURE` → `en-US`

---

## Troubleshooting (erros comuns)

### Workflow não aparece na aba Actions
Confirme:
- o arquivo está em `.github/workflows/daily-email.yml`
- você commitou na branch padrão (`master` / `main`)

### Erro de path (ex.: `C:\english\...`)
Em runner GitHub, não existe `C:\english`.  
Use paths relativos via env vars no workflow:
- `STATE_PATH=sent_state.json`, etc.

### Erro ao fazer `git push` no final
Confirme:
- Workflow permissions = **Read and write**
- O repo não tem proteção que bloqueia push direto na branch

### SMTP (Gmail) falhando
Se for Gmail:
- use **App Password** (conta com 2FA)
- senha normal geralmente não funciona

---

## Segurança
- Repositório público: **nunca** commite senhas/chaves.
- Use **GitHub Secrets** para credenciais.
- Evite gravar dados sensíveis em logs/JSONs.

---

## Contribuições
PRs e issues são bem-vindos. Sugestões para melhorar o conteúdo do e-mail, qualidade do áudio e organização do vocabulário ajudam bastante.

---

## Licença
Este projeto é **open source**.