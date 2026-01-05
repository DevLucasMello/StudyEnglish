// Program.cs (.NET 8)
// ------------------------------------------------------------
// Mantém o que já está bom e melhora a fluidez do PT-BR:
//
// - Groq (OpenAI-compatible) gera: classificação, formas verbais, traduções (palavra) e 5 exemplos em inglês.
// - DeepL traduz as FRASES (examplesEn -> examplesPt) com português natural.
// - E-mail via Gmail SMTP (App Password).
// - Estado idempotente em C:\english\sent_state.json (não reenviar).
// - Cache DeepL (frases) em C:\english\deepl_sentence_cache.json para poupar quota.
//
// VARS DE AMBIENTE (obrigatórias):
//   OPENAI_API_KEY   = chave da Groq
//   SMTP_HOST        = smtp.gmail.com
//   SMTP_PORT        = 587
//   SMTP_USER        = seuemail@gmail.com
//   SMTP_PASS        = App Password do Gmail
//   EMAIL_TO         = destinatário
//
// VARS (recomendadas):
//   DEEPL_AUTH_KEY   = chave DeepL (free ou pro). Se não setar, examplesPt pode ficar vazio.
//   GROQ_MODEL       = default: llama-3.1-8b-instant
//   VOCABULARY_PATH  = default: C:\english\english-vocabulary.txt
//
// VARS opcionais:
//   DEEPL_ENDPOINT   = ex: https://api-free.deepl.com/v2/translate (override manual)
//   EMAIL_AUDIO_ENABLED = true/false (default true)
//   EMAIL_AUDIO_DIR     = C:\english\audio
//   EMAIL_AUDIO_VOICE_CULTURE = en-US
//
// Observação:
// - Chaves DeepL "Free" normalmente terminam com ":fx" e usam api-free.deepl.com.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
public class Program
{
    // =========================
    // Arquivos
    // =========================
// =========================
// Arquivos (suporta GitHub Actions)
// =========================
// Defaults legados (Windows local)
private const string LegacyDefaultVocabularyPath = @"C:\english\english-vocabulary.txt";
private const string LegacyStatePath = @"C:\english\sent_state.json";
private const string LegacyDeepLSentenceCachePath = @"C:\english\deepl_sentence_cache.json";
private const string LegacyBlockedLogPath = @"C:\english\blocked_words.log";
private const string LegacyDefaultAudioDir = @"C:\english\audio";

// Base de execução (no GitHub Actions normalmente vem em GITHUB_WORKSPACE)
private static string WorkDir =>
    Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ??
    Directory.GetCurrentDirectory();

// Se vier relativo (ex: "sent_state.json"), resolve para dentro do WorkDir
private static string ResolvePath(string pathOrRelative)
{
    if (string.IsNullOrWhiteSpace(pathOrRelative))
        return pathOrRelative;

    var p = pathOrRelative.Trim();
    return Path.IsPathRooted(p) ? p : Path.Combine(WorkDir, p);
}

// Arquivos usados no runtime (ENV tem prioridade)
private static string DefaultVocabularyPath =>
    ResolvePath(Environment.GetEnvironmentVariable("VOCABULARY_PATH") ?? LegacyDefaultVocabularyPath);

private static string StatePath =>
    ResolvePath(Environment.GetEnvironmentVariable("STATE_PATH") ?? LegacyStatePath);

// Cache das traduções do DeepL (frases EN -> PT)
private static string DeepLSentenceCachePath =>
    ResolvePath(Environment.GetEnvironmentVariable("DEEPL_CACHE_PATH") ?? LegacyDeepLSentenceCachePath);

// Log de palavras/frases que foram puladas por erro de formatação do modelo
private static string BlockedLogPath =>
    ResolvePath(Environment.GetEnvironmentVariable("BLOCKED_LOG_PATH") ?? LegacyBlockedLogPath);

// Áudio (opcional)
private static string DefaultAudioDir =>
    ResolvePath(Environment.GetEnvironmentVariable("EMAIL_AUDIO_DIR")
        ?? Environment.GetEnvironmentVariable("AUDIO_DIR")
        ?? LegacyDefaultAudioDir);

private static void EnsureParentDirectory(string filePath)
{
    try
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }
    catch
    {
        // ignore
    }
}private const int ItemsPerDay = 10;

    // =========================
    // Groq (OpenAI-compatible)
    // =========================
    private const string GroqEndpoint = "https://api.groq.com/openai/v1/chat/completions";
    private const string DefaultGroqModel = "llama-3.1-8b-instant";

    // =========================
    // DeepL
    // =========================
    private const string DeepLFreeEndpoint = "https://api-free.deepl.com/v2/translate";
    private const string DeepLProEndpoint = "https://api.deepl.com/v2/translate";
    private const string DeepLTargetLang = "PT-BR";
    private const string DeepLSourceLang = "EN";
    private const int DeepLBatchSize = 50; // DeepL permite até 50 textos por request

    // =========================
    // Retry
    // =========================
    private const int MaxRetries = 8;

    public static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // ENV
        var groqKey = Env("OPENAI_API_KEY", required: true); // aqui é a chave da Groq
        var groqModel = Env("GROQ_MODEL", required: false);
        if (string.IsNullOrWhiteSpace(groqModel))
            groqModel = DefaultGroqModel;

        var smtpHost = Env("SMTP_HOST", required: true);
        var smtpPort = int.Parse(Env("SMTP_PORT", required: true));
        var smtpUser = Env("SMTP_USER", required: true);
        var smtpPass = Env("SMTP_PASS", required: true);
        var emailTo = Env("EMAIL_TO", required: true);

        // DeepL (opcional, mas recomendado)
        var deeplAuthKey = Env("DEEPL_AUTH_KEY", required: false);
        var deeplEndpoint = ResolveDeepLEndpoint(deeplAuthKey);

        // Arquivo de vocabulário pode ser sobrescrito por env:
        var vocabPath = Env("VOCABULARY_PATH", required: false);
        if (string.IsNullOrWhiteSpace(vocabPath))
            vocabPath = DefaultVocabularyPath;


        vocabPath = ResolvePath(vocabPath);
        // Áudio por e-mail (opcional)
        var audioEnabled = EnvBool("EMAIL_AUDIO_ENABLED", EnvBool("AUDIO_ENABLED", defaultValue: true));
        var audioDir = Env("EMAIL_AUDIO_DIR", required: false);
        if (string.IsNullOrWhiteSpace(audioDir)) audioDir = Env("AUDIO_DIR", required: false);
        if (string.IsNullOrWhiteSpace(audioDir)) audioDir = DefaultAudioDir;
        var audioCulture = Env("EMAIL_AUDIO_VOICE_CULTURE", required: false);
        if (string.IsNullOrWhiteSpace(audioCulture)) audioCulture = Env("AUDIO_VOICE_CULTURE", required: false);
        if (string.IsNullOrWhiteSpace(audioCulture)) audioCulture = "en-US";

        if (!File.Exists(vocabPath))
        {
            Console.WriteLine("Arquivo de vocabulário não encontrado: " + vocabPath);
            return 1;
        }

        var state = LoadState(StatePath);

        // 1) Se existir envio pendente, finalize primeiro (sem gerar novo pick)
        if (state.Current != null && !state.Current.EmailSent)
        {
            Console.WriteLine($"Encontrado envio pendente (data={state.Current.Date}, iteração={state.Current.Iteration}). Tentando concluir (somente e-mail).");

            await SendEmailForRun(
                run: state.Current,
                statePath: StatePath,
                state: state,
                groqKey: groqKey,
                groqModel: groqModel,
                smtpHost: smtpHost,
                smtpPort: smtpPort,
                smtpUser: smtpUser,
                smtpPass: smtpPass,
                emailTo: emailTo,
                audioEnabled: audioEnabled,
                audioDir: audioDir,
                audioCulture: audioCulture,
                deeplAuthKey: deeplAuthKey,
                deeplEndpoint: deeplEndpoint,
                vocabPath: vocabPath
            );

            if (state.Current.EmailSent)
                FinalizeRun(state, state.Current);

            SaveState(StatePath, state);
        }

        // 2) Se já finalizou hoje, sai
        var todayStr = DateTime.Now.ToString("yyyy-MM-dd");
        if (state.LastSentDate == todayStr)
        {
            Console.WriteLine("Já finalizado hoje (e-mail). Nada a fazer.");
            state.LastRunIso = DateTime.UtcNow.ToString("o");
            SaveState(StatePath, state);
            return 0;
        }

        // 3) Se não existe current para hoje, cria e salva imediatamente (idempotência)
        if (state.Current == null || !string.Equals(state.Current.Date, todayStr, StringComparison.Ordinal))
        {
            var allLines = LoadVocabularyLines(vocabPath);
            var pick = PickRandomLines(allLines, state.Sent, state.Blocked, ItemsPerDay);

            if (pick.Count == 0)
            {
                Console.WriteLine("Não há mais linhas para enviar (todas marcadas como enviadas).");
                state.LastSentDate = todayStr;
                state.LastRunIso = DateTime.UtcNow.ToString("o");
                SaveState(StatePath, state);
                return 0;
            }

            state.Current = new DispatchRun
            {
                Date = todayStr,
                Iteration = state.SendIteration + 1,
                PickRaw = pick,
                EmailSent = false,
                CreatedIso = DateTime.UtcNow.ToString("o")
            };

            SaveState(StatePath, state);
        }

        // 4) Executa envio do run atual
        await SendEmailForRun(
            run: state.Current!,
            statePath: StatePath,
            state: state,
            groqKey: groqKey,
            groqModel: groqModel,
            smtpHost: smtpHost,
            smtpPort: smtpPort,
            smtpUser: smtpUser,
            smtpPass: smtpPass,
            emailTo: emailTo,
            audioEnabled: audioEnabled,
            audioDir: audioDir,
            audioCulture: audioCulture,
            deeplAuthKey: deeplAuthKey,
            deeplEndpoint: deeplEndpoint,
            vocabPath: vocabPath
        );

        if (state.Current!.EmailSent)
        {
            FinalizeRun(state, state.Current!);
            SaveState(StatePath, state);
            Console.WriteLine("Enviado com sucesso e estado atualizado.");
        }
        else
        {
            SaveState(StatePath, state);
            Console.WriteLine("Execução terminou, mas o e-mail ainda está pendente. O próximo agendamento vai re-tentar somente o envio do e-mail.");
        }

        return 0;
    }

    // =========================
    // Envio (somente Email) com idempotência por run
    // =========================
    private static async Task SendEmailForRun(
        DispatchRun run,
        string statePath,
        SentState state,
        string groqKey,
        string groqModel,
        string smtpHost,
        int smtpPort,
        string smtpUser,
        string smtpPass,
        string emailTo,
        bool audioEnabled,
        string audioDir,
        string audioCulture,
        string? deeplAuthKey,
        string deeplEndpoint,
        string vocabPath
    )
    {
        using var httpGroq = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        httpGroq.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", groqKey);

        using var httpDeepL = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // 0) Se precisar, gera o payload uma única vez e persiste no estado
        if (run.Analysis == null)
        {
            Console.WriteLine("Gerando análise (Groq): classificação, formas verbais, traduções e exemplos EN...");

            // Tenta gerar payload. Se um item vier inválido (ex: não retornou 5 exemplos),
            // marca como bloqueado e troca por outra linha do vocabulário, sem travar o envio do e-mail.
            run.Analysis = await GenerateDailyPayloadResilientAsync(
                http: httpGroq,
                model: groqModel,
                run: run,
                state: state,
                statePath: statePath,
                vocabularyPath: vocabPath
            );

            // Tradução natural das FRASES com DeepL (PT-BR)
            if (!string.IsNullOrWhiteSpace(deeplAuthKey))
            {
                Console.WriteLine("Traduzindo exemplos EN -> PT-BR (DeepL) para ficar mais natural...");
                await FillExamplesPtWithDeepLAsync(
                    http: httpDeepL,
                    authKey: deeplAuthKey!.Trim(),
                    endpoint: deeplEndpoint,
                    analysis: run.Analysis!,
                    cachePath: DeepLSentenceCachePath
                );
            }
            else
            {
                Console.WriteLine("Aviso: DEEPL_AUTH_KEY não definida. examplesPt pode ficar vazio.");
            }

            run.EmailSubject = BuildEmailSubject(run);
            run.EmailHtml = BuildEmailHtml(run);

            // Áudio (opcional)
            if (audioEnabled)
            {
                EnsureAudioForRun(run, audioDir, audioCulture);
                run.EmailHtml = BuildEmailHtml(run);
            }

            run.PayloadIso = DateTime.UtcNow.ToString("o");
            SaveState(statePath, state);
        }
        else
        {
            // Se já tem Analysis mas examplesPt está incompleto e tem DeepL, tenta completar
            run.EmailSubject ??= BuildEmailSubject(run);

            if (!string.IsNullOrWhiteSpace(deeplAuthKey) && run.Analysis?.Items != null)
            {
                var needPt = run.Analysis.Items.Any(i =>
                    (i.ExamplesEn?.Count ?? 0) > 0 &&
                    (i.ExamplesPt == null || i.ExamplesPt.Count != i.ExamplesEn!.Count || i.ExamplesPt.Any(string.IsNullOrWhiteSpace)));

                if (needPt)
                {
                    Console.WriteLine("Completando examplesPt ausentes via DeepL...");
                    await FillExamplesPtWithDeepLAsync(httpDeepL, deeplAuthKey!.Trim(), deeplEndpoint, run.Analysis!, DeepLSentenceCachePath);
                }
            }

            if (audioEnabled)
                EnsureAudioForRun(run, audioDir, audioCulture);

            run.EmailHtml = BuildEmailHtml(run);
            SaveState(statePath, state);
        }

        // 1) Email
        if (!run.EmailSent)
        {
            Console.WriteLine("Enviando e-mail...");
            await Retry(() =>
            {
                SendEmail(
                    host: smtpHost,
                    port: smtpPort,
                    user: smtpUser,
                    pass: smtpPass,
                    from: smtpUser,
                    to: emailTo,
                    subject: run.EmailSubject ?? BuildEmailSubject(run),
                    htmlBody: run.EmailHtml ?? BuildEmailHtml(run),
                    audioPath: run.AudioPath
                );

                run.EmailSent = true;
                run.LastEmailIso = DateTime.UtcNow.ToString("o");
                TryDeleteAudioFile(run.AudioPath);
                run.AudioPath = null;

                SaveState(statePath, state);
                return Task.CompletedTask;
            });
        }
        else
        {
            Console.WriteLine("E-mail já enviado nesse run. Pulando.");
        }
    }

    private static void FinalizeRun(SentState state, DispatchRun run)
    {
        foreach (var line in run.PickRaw)
            state.Sent.Add(Norm(line));

        state.LastSentDate = run.Date;
        state.LastRunIso = DateTime.UtcNow.ToString("o");
        state.SendIteration = run.Iteration;

        // Limpa current somente quando o e-mail foi concluído
        state.Current = null;
    }

    // =========================
    // Groq: Geração do payload do dia
    // =========================
    private static async Task<DailyAnalysis> GenerateDailyPayloadAsync(HttpClient http, string model, List<string> pickRaw)
    {
        var prompt = BuildGroqPrompt(pickRaw);

        var payload = new
        {
            model,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "Você é um professor de inglês e lexicógrafo. " +
                        "Responda APENAS com JSON válido no formato solicitado. " +
                        "Não inclua texto fora do JSON."
                },
                new { role = "user", content = prompt }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var resp = await http.PostAsync(GroqEndpoint, new StringContent(json, Encoding.UTF8, "application/json"));
        var respText = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Groq falhou: {(int)resp.StatusCode} {respText}");

        using var doc = JsonDocument.Parse(respText);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            throw new Exception("Resposta vazia do Groq.");

        DailyAnalysis? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<DailyAnalysis>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            throw new Exception("Falha ao desserializar JSON retornado pelo Groq. Conteúdo: " + content, ex);
        }

        if (parsed?.Items == null || parsed.Items.Count == 0)
            throw new Exception("JSON inesperado retornado pelo Groq (items vazio).");

        NormalizeAndValidate(parsed, pickRaw);
        return parsed;
    }

    
    // =========================
    // Groq: geração resiliente (pula itens problemáticos)
    // =========================
    private static async Task<DailyAnalysis> GenerateDailyPayloadResilientAsync(
        HttpClient http,
        string model,
        DispatchRun run,
        SentState state,
        string statePath,
        string vocabularyPath
    )
    {
        // Carrega vocabulário para poder substituir itens que deram erro
        var allLines = LoadVocabularyLines(vocabularyPath);

        // Evita loop infinito se o modelo insistir em retornar formatos inválidos
        const int maxReplacements = 50;
        int replacements = 0;

        while (true)
        {
            try
            {
                return await GenerateDailyPayloadAsync(http, model, run.PickRaw);
            }
            catch (Exception ex)
            {
                var problem = TryExtractProblemInput(ex.Message);

                // Se não conseguir identificar um item específico, repropaga (pode ser rede/API)
                if (string.IsNullOrWhiteSpace(problem))
                    throw;

                // Marca como bloqueado (não usar mais em execuções futuras)
                MarkBlocked(state, problem, ex.Message);
                AppendBlockedLog(problem, ex.Message);
                SaveState(statePath, state);

                // Remove o item problemático do pick atual
                var key = Norm(problem);
                run.PickRaw = run.PickRaw.Where(x => Norm(x) != key).ToList();

                // Tenta substituir por outra linha ainda não enviada/bloqueada
                var replacement = PickOneReplacement(allLines, state.Sent, state.Blocked, run.PickRaw);
                if (!string.IsNullOrWhiteSpace(replacement))
                    run.PickRaw.Add(replacement);

                SaveState(statePath, state);

                replacements++;
                Console.WriteLine($"Aviso: pulando '{problem}' (erro de payload). Substituições até agora: {replacements}/{maxReplacements}.");

                if (run.PickRaw.Count == 0)
                    throw new Exception("Não restou nenhuma linha válida para montar o e-mail (todas falharam).");

                if (replacements >= maxReplacements)
                    throw new Exception("Muitas substituições por erro de payload. Último erro: " + ex.Message, ex);

                // tenta de novo com o novo pickRaw
            }
        }
    }

    private static string? TryExtractProblemInput(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        // Captura o primeiro trecho entre aspas simples, ex:
        // "Item 'approx' precisa retornar exatamente 5 exemplos EN."
        var m = Regex.Match(message, @"'([^']+)'");
        if (m.Success)
            return m.Groups[1].Value;

        return null;
    }

    private static void MarkBlocked(SentState state, string word, string reason)
    {
        state.Blocked ??= new Dictionary<string, BlockedEntry>(StringComparer.OrdinalIgnoreCase);

        var key = Norm(word);
        if (!state.Blocked.TryGetValue(key, out var entry))
        {
            entry = new BlockedEntry
            {
                Word = word.Trim(),
                Reason = reason.Trim(),
                FirstIso = DateTime.UtcNow.ToString("o"),
                LastIso = DateTime.UtcNow.ToString("o"),
                Count = 1
            };
            state.Blocked[key] = entry;
        }
        else
        {
            entry.Reason = reason.Trim();
            entry.LastIso = DateTime.UtcNow.ToString("o");
            entry.Count++;
        }
    }

    private static void AppendBlockedLog(string word, string reason)
    {
        try
        {
            EnsureParentDirectory(BlockedLogPath);
            var line = $"{DateTime.UtcNow:o}\t{word}\t{reason.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}";
            File.AppendAllText(BlockedLogPath, line, Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    private static string? PickOneReplacement(
        List<string> allLines,
        ISet<string> sent,
        Dictionary<string, BlockedEntry> blocked,
        List<string> currentPick
    )
    {
        var exclude = new HashSet<string>(currentPick.Select(Norm), StringComparer.OrdinalIgnoreCase);

        var remaining = allLines
            .Where(x => !sent.Contains(Norm(x)))
            .Where(x => !blocked.ContainsKey(Norm(x)))
            .Where(x => !exclude.Contains(Norm(x)))
            .ToList();

        if (remaining.Count == 0)
            return null;

        int idx = RandomNumberGenerator.GetInt32(remaining.Count);
        return remaining[idx];
    }

private static string BuildGroqPrompt(List<string> pickRaw)
    {
        var list = string.Join("", pickRaw.Select(x => " - " + x));

        // IMPORTANTE:
        // - Não use string interpolada ($@"...") aqui, porque o exemplo de JSON contém muitas chaves { }.
        // - Se usar interpolação, precisaria escapar todas as chaves com {{ }}.
        // - Para evitar erros de compilação, usamos concatenação simples.
        var prompt = @"
Analise cada LINHA abaixo (uma por item). Para cada linha:
1) Classifique em UMA categoria:
   Verb, PhrasalVerb, Noun, Adjective, Adverb, Pronoun, Preposition, Conjunction, Determiner, Expression, Other
2) Se for Verb ou PhrasalVerb:
   - Forneça: present (base), pastSimple, pastParticiple.
   - Forneça traduções PT-BR (2–6) em translations.general
   - E traduções por forma: translations.present/pastSimple/pastParticiple (0–6)
3) Se NÃO for Expression:
   - Gere EXATAMENTE 5 frases curtas em inglês (B1–B2, 6–12 palavras),
     e cada frase deve conter a linha ORIGINAL exatamente como fornecida.
   - NÃO traduza as frases: examplesPt deve ser [] (array vazio).
4) Para Expression:
   - NÃO gere exemplos: examplesEn e examplesPt devem ser [].
   - Apenas translations.general (PT-BR).

Responda SOMENTE com JSON válido exatamente assim:

{
  ""items"": [
    {
      ""input"": ""..."",
      ""category"": ""Verb|PhrasalVerb|Noun|Adjective|Adverb|Pronoun|Preposition|Conjunction|Determiner|Expression|Other"",
      ""verbForms"": { ""present"": ""..."", ""pastSimple"": ""..."", ""pastParticiple"": ""..."" },
      ""translations"": {
        ""general"": [""..."", ""...""],
        ""present"": [""...""],
        ""pastSimple"": [""...""],
        ""pastParticiple"": [""...""]
      },
      ""examplesEn"": [""..."",""..."",""..."",""..."",""...""],
      ""examplesPt"": []
    }
  ]
}

Linhas:
";
        return prompt + list;
    }

    private static void NormalizeAndValidate(DailyAnalysis analysis, List<string> pickRaw)
    {
        var items = analysis.Items ?? new();

        var dict = new Dictionary<string, DailyItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (string.IsNullOrWhiteSpace(it.Input)) continue;
            var key = Norm(it.Input);
            if (!dict.ContainsKey(key))
                dict[key] = it;
        }

        var normalized = new List<DailyItem>();
        foreach (var raw in pickRaw)
        {
            var key = Norm(raw);
            if (!dict.TryGetValue(key, out var it))
                throw new Exception($"Groq não retornou item para '{raw}'.");

            it.Input = raw.Trim();
            it.Category = (it.Category ?? "Other").Trim();

            it.Translations ??= new DailyTranslations();
            it.Translations.General ??= new List<string>();
            it.Translations.Present ??= new List<string>();
            it.Translations.PastSimple ??= new List<string>();
            it.Translations.PastParticiple ??= new List<string>();

            it.ExamplesEn ??= new List<string>();
            it.ExamplesPt ??= new List<string>();

            var isVerb = it.Category.Equals("Verb", StringComparison.OrdinalIgnoreCase) ||
                         it.Category.Equals("PhrasalVerb", StringComparison.OrdinalIgnoreCase);

            if (isVerb)
            {
                if (it.VerbForms == null ||
                    string.IsNullOrWhiteSpace(it.VerbForms.Present) ||
                    string.IsNullOrWhiteSpace(it.VerbForms.PastSimple) ||
                    string.IsNullOrWhiteSpace(it.VerbForms.PastParticiple))
                {
                    throw new Exception($"Item '{it.Input}' foi classificado como verbo, mas verbForms está incompleto.");
                }

                if (it.Translations.General.Count == 0)
                    throw new Exception($"Item '{it.Input}' não trouxe translations.general.");
            }
            else
            {
                it.VerbForms = null;
                it.Translations.Present = new List<string>();
                it.Translations.PastSimple = new List<string>();
                it.Translations.PastParticiple = new List<string>();
            }

            var isExpression = it.Category.Equals("Expression", StringComparison.OrdinalIgnoreCase);
            if (isExpression)
            {
                it.ExamplesEn = new List<string>();
                it.ExamplesPt = new List<string>();
                if (it.Translations.General.Count == 0)
                    throw new Exception($"Expression '{it.Input}' não trouxe translations.general.");
            }
            else
            {
                if (it.ExamplesEn.Count != 5)
                    throw new Exception($"Item '{it.Input}' precisa retornar exatamente 5 exemplos EN.");
            }

            normalized.Add(it);
        }

        analysis.Items = normalized;
    }

    // =========================
    // DeepL: traduzir exemplos EN -> PT-BR
    // =========================
    private static async Task FillExamplesPtWithDeepLAsync(HttpClient http, string authKey, string endpoint, DailyAnalysis analysis, string cachePath)
    {
        if (analysis.Items == null || analysis.Items.Count == 0)
            return;

        var cache = LoadJsonDictSafe(cachePath);

        var map = new List<(DailyItem item, int idx, string en)>();
        foreach (var it in analysis.Items)
        {
            if (it.ExamplesEn == null || it.ExamplesEn.Count == 0)
                continue;

            it.ExamplesPt ??= new List<string>();

            for (int i = 0; i < it.ExamplesEn.Count; i++)
            {
                var en = (it.ExamplesEn[i] ?? "").Trim();
                if (string.IsNullOrWhiteSpace(en))
                    continue;

                if (it.ExamplesPt.Count > i && !string.IsNullOrWhiteSpace(it.ExamplesPt[i]))
                    continue;

                map.Add((it, i, en));
            }
        }

        if (map.Count == 0)
            return;

        // 1) Preenche do cache
        var toTranslate = new List<(DailyItem item, int idx, string en)>();
        foreach (var (item, idx, en) in map)
        {
            var key = NormalizeCacheKey(en);
            if (cache.TryGetValue(key, out var cachedPt) && IsAcceptableSentenceTranslation(cachedPt))
            {
                EnsureSize(item.ExamplesPt!, item.ExamplesEn!.Count);
                item.ExamplesPt![idx] = cachedPt;
            }
            else
            {
                toTranslate.Add((item, idx, en));
            }
        }

        if (toTranslate.Count == 0)
            return;

        // 2) Traduz em batches de até 50
        for (int i = 0; i < toTranslate.Count; i += DeepLBatchSize)
        {
            var batch = toTranslate.Skip(i).Take(DeepLBatchSize).ToList();
            var texts = batch.Select(x => x.en).ToList();

            var translated = await TranslateBatchDeepLAsync(http, authKey, endpoint, texts);

            for (int j = 0; j < batch.Count && j < translated.Count; j++)
            {
                var pt = (translated[j] ?? "").Trim();
                if (!IsAcceptableSentenceTranslation(pt))
                    continue;

                var (item, idx, en) = batch[j];

                EnsureSize(item.ExamplesPt!, item.ExamplesEn!.Count);
                item.ExamplesPt![idx] = pt;

                cache[NormalizeCacheKey(en)] = pt;
            }

            SaveJsonDict(cachePath, cache);
            await Task.Delay(150);
        }
    }

    private static async Task<List<string?>> TranslateBatchDeepLAsync(HttpClient http, string authKey, string endpoint, List<string> texts)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var content = new FormUrlEncodedContent(BuildDeepLForm(authKey, texts));
                using var resp = await http.PostAsync(endpoint, content);

                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    await Backoff(attempt);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"DeepL falhou: {(int)resp.StatusCode} {json}");

                return ParseDeepL(json, texts.Count);
            }
            catch (TaskCanceledException)
            {
                await Backoff(attempt);
            }
            catch (HttpRequestException)
            {
                await Backoff(attempt);
            }
        }

        return texts.Select(_ => (string?)null).ToList();
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildDeepLForm(string authKey, List<string> texts)
    {
        yield return new KeyValuePair<string, string>("auth_key", authKey);
        yield return new KeyValuePair<string, string>("target_lang", DeepLTargetLang);
        yield return new KeyValuePair<string, string>("source_lang", DeepLSourceLang);
        yield return new KeyValuePair<string, string>("preserve_formatting", "1");

        foreach (var t in texts)
            yield return new KeyValuePair<string, string>("text", t);
    }

    private static List<string?> ParseDeepL(string json, int expectedCount)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("translations", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Enumerable.Repeat<string?>(null, expectedCount).ToList();

            var result = new List<string?>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var t))
                    result.Add(t.GetString());
                else
                    result.Add(null);
            }

            while (result.Count < expectedCount) result.Add(null);
            return result;
        }
        catch
        {
            return Enumerable.Repeat<string?>(null, expectedCount).ToList();
        }
    }

    private static async Task Backoff(int attempt)
    {
        var ms = (int)Math.Min(15000, 500 * Math.Pow(2, attempt - 1));
        await Task.Delay(ms);
    }

    private static string ResolveDeepLEndpoint(string? authKey)
    {
        var env = Environment.GetEnvironmentVariable("DEEPL_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        if (string.IsNullOrWhiteSpace(authKey))
            return DeepLFreeEndpoint;

        var k = authKey.Trim();
        if (k.EndsWith(":fx", StringComparison.OrdinalIgnoreCase))
            return DeepLFreeEndpoint;

        return DeepLProEndpoint;
    }

    private static string NormalizeCacheKey(string s)
        => (s ?? "").Trim().ToLowerInvariant();

    private static bool IsAcceptableSentenceTranslation(string? tr)
    {
        if (string.IsNullOrWhiteSpace(tr))
            return false;

        if (tr.Length > 240)
            return false;

        if (tr.Contains("<") || tr.Contains(">") || tr.Contains("http", StringComparison.OrdinalIgnoreCase))
            return false;

        int letters = tr.Count(char.IsLetter);
        if (letters < 4) return false;

        return true;
    }

    private static Dictionary<string, string> LoadJsonDictSafe(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(path))
                return dict;

            var json = File.ReadAllText(path, Encoding.UTF8);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (raw == null) return dict;

            foreach (var kv in raw)
            {
                var k = NormalizeCacheKey(kv.Key);
                var v = (kv.Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v))
                    continue;
                dict[k] = v;
            }
        }
        catch
        {
            // ignore
        }

        return dict;
    }

    private static void SaveJsonDict(string path, Dictionary<string, string> dict)
    {
        try
        {
            EnsureParentDirectory(path);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    private static void EnsureSize(List<string> list, int size)
    {
        while (list.Count < size)
            list.Add("");
    }

    // =========================
    // Áudio (TTS local)
    // =========================
    private static void EnsureAudioForRun(DispatchRun run, string audioDir, string audioCulture)
    {
        try
        {
            if (run.Analysis?.Items == null || run.Analysis.Items.Count == 0)
                return;

            if (!string.IsNullOrWhiteSpace(run.AudioPath) && File.Exists(run.AudioPath))
                return;

            Directory.CreateDirectory(audioDir);

            var safeDate = (run.Date ?? "").Replace(':', '-').Replace('/', '-').Replace('\\', '-');
            if (string.IsNullOrWhiteSpace(safeDate))
                safeDate = DateTime.Now.ToString("yyyy-MM-dd");

            var fileName = $"{safeDate}_day{run.Iteration:000}.wav";
            var fullPath = Path.Combine(audioDir, fileName);

            // Script do áudio: SOMENTE inglês (sem traduções)
            var script = BuildAudioScript(run);

            using var synth = new SpeechSynthesizer
            {
                Volume = 100,
                Rate = 0
            };

            // Tenta usar uma voz específica (opcional) ou selecionar por cultura (en-US)
            try
            {
                var voiceName = Env("EMAIL_AUDIO_VOICE_NAME", required: false) ?? Env("AUDIO_VOICE_NAME", required: false);
                if (!string.IsNullOrWhiteSpace(voiceName))
                {
                    synth.SelectVoice(voiceName.Trim());
                }
                else
                {
                    var culture = new CultureInfo(audioCulture);
                    // Geralmente dá uma voz EN bem melhor do que o default do Windows
                    synth.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Adult, 0, culture);
                }
            }
            catch
            {
                try
                {
                    var culture = new CultureInfo(audioCulture);
                    synth.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, culture);
                }
                catch { /* ignora */ }
            }

            synth.SetOutputToWaveFile(fullPath);
            synth.Speak(script);
            synth.SetOutputToNull();

            run.AudioPath = fullPath;
            run.AudioCreatedIso = DateTime.UtcNow.ToString("o");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Falha ao gerar áudio (TTS). Continuando sem áudio. Detalhe: " + ex.Message);
            run.AudioPath = null;
            run.AudioCreatedIso = null;
        }
    }

    private static object? CreateSpeechSynthesizerOrNull()
    {
        try
        {
            // Tipicamente: "System.Speech.Synthesis.SpeechSynthesizer, System.Speech"
            var t = Type.GetType("System.Speech.Synthesis.SpeechSynthesizer, System.Speech");
            if (t == null) return null;
            return Activator.CreateInstance(t);
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetProperty(object target, string propName, object value)
    {
        try
        {
            var p = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) return;
            p.SetValue(target, value);
        }
        catch { }
    }

    private static void TryInvoke(object target, string methodName, params object[] args)
    {
        try
        {
            var methods = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length)
                .ToList();

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                bool ok = true;

                for (int i = 0; i < ps.Length; i++)
                {
                    var argType = args[i]?.GetType() ?? typeof(object);
                    if (!ps[i].ParameterType.IsAssignableFrom(argType) && ps[i].ParameterType != typeof(object))
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok) continue;

                m.Invoke(target, args);
                return;
            }
        }
        catch { }
    }

    private static string BuildAudioScript(DispatchRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"English vocabulary for {run.Date}. ");
        sb.AppendLine($"There are {run.PickRaw.Count} items today.");
        sb.AppendLine();

        var items = run.Analysis?.Items ?? new List<DailyItem>();
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            sb.AppendLine($"Item {i + 1}. {it.Input}.");

            if (it.VerbForms != null)
            {
                sb.AppendLine($"Present: {it.VerbForms.Present}. Past: {it.VerbForms.PastSimple}. Past participle: {it.VerbForms.PastParticiple}.");
            }

            if (it.ExamplesEn != null && it.ExamplesEn.Count > 0)
            {
                sb.AppendLine("Examples.");
                foreach (var ex in it.ExamplesEn)
                {
                    if (!string.IsNullOrWhiteSpace(ex))
                        sb.AppendLine(ex.Trim().TrimEnd('.') + ".");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("End.");
        return sb.ToString();
    }

    // =========================
    // Email (montagem)
    // =========================
    private static string BuildEmailSubject(DispatchRun run)
    {
        var dateDisplay = DateTime.TryParse(run.Date, out var dt) ? dt.ToString("dd/MM/yyyy") : run.Date;
        return $"Inglês diário: {run.PickRaw.Count} itens ({dateDisplay})";
    }

    private static string BuildEmailHtml(DispatchRun run)
    {
        var sb = new StringBuilder();
        sb.Append("<div style='font-family:Arial,sans-serif'>");

        var dateDisplay = DateTime.TryParse(run.Date, out var dt) ? dt.ToString("dd/MM/yyyy") : run.Date;
        sb.Append($"<h2>Vocabulário do dia ({run.Iteration}) - {WebUtility.HtmlEncode(dateDisplay)}</h2>");
        sb.Append($"<p style='color:#666'>Total: {run.PickRaw.Count} linhas</p>");

        if (!string.IsNullOrWhiteSpace(run.AudioPath))
        {
            sb.Append("<div style='margin:12px 0;padding:10px;border:1px solid #eee;border-radius:10px;background:#fafafa'>");
            sb.Append("<div style='font-size:13px'>🔊 <b>Áudio do estudo</b> está anexado neste e-mail.</div>");
            sb.Append("<div style='color:#666;font-size:12px;margin-top:4px'>Abra o anexo <b>.wav</b> para ouvir.</div>");
            sb.Append("</div>");
        }

        if (run.Analysis?.Items == null || run.Analysis.Items.Count == 0)
        {
            sb.Append("<p><i>Sem análise disponível.</i></p></div>");
            return sb.ToString();
        }

        sb.Append("<ol>");

        foreach (var it in run.Analysis.Items)
        {
            sb.Append("<li style='margin-bottom:18px'>");
            sb.Append($"<div><b>{WebUtility.HtmlEncode(it.Input)}</b> ");
            sb.Append($"<span style='color:#666'>(tipo: {WebUtility.HtmlEncode(it.Category ?? "Other")})</span></div>");

            if (it.VerbForms != null)
            {
                sb.Append("<div style='color:#444; font-size: 13px; margin-top: 4px'>");
                sb.Append("<b>Forms:</b> ");
                sb.Append($"Present: {WebUtility.HtmlEncode(it.VerbForms.Present)}");
                sb.Append($", Past: {WebUtility.HtmlEncode(it.VerbForms.PastSimple)}");
                sb.Append($", Participle: {WebUtility.HtmlEncode(it.VerbForms.PastParticiple)}");
                sb.Append("</div>");
            }

            sb.Append("<div style='margin-top:6px'>");
            sb.Append("<b>Traduções (PT-BR):</b>");
            sb.Append("<ul>");
            foreach (var t in (it.Translations?.General ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8))
            {
                sb.Append($"<li>{WebUtility.HtmlEncode(t)}</li>");
            }
            sb.Append("</ul>");
            sb.Append("</div>");

            if (it.VerbForms != null)
            {
                sb.Append("<div style='margin-top:6px'>");
                sb.Append("<b>Traduções por tempo:</b>");
                sb.Append("<ul>");
                sb.Append($"<li><b>Present</b>: {WebUtility.HtmlEncode(string.Join("; ", (it.Translations?.Present ?? new List<string>()).Take(6)))}</li>");
                sb.Append($"<li><b>Past Simple</b>: {WebUtility.HtmlEncode(string.Join("; ", (it.Translations?.PastSimple ?? new List<string>()).Take(6)))}</li>");
                sb.Append($"<li><b>Past Participle</b>: {WebUtility.HtmlEncode(string.Join("; ", (it.Translations?.PastParticiple ?? new List<string>()).Take(6)))}</li>");
                sb.Append("</ul>");
                sb.Append("</div>");
            }

            if (it.ExamplesEn != null && it.ExamplesEn.Count > 0)
            {
                sb.Append("<div style='margin-top:6px'>");
                sb.Append("<b>Exemplos:</b>");
                sb.Append("<ol style='margin-top:4px'>");

                for (int i = 0; i < it.ExamplesEn.Count; i++)
                {
                    var en = it.ExamplesEn[i];
                    var pt = (it.ExamplesPt != null && i < it.ExamplesPt.Count) ? it.ExamplesPt[i] : "";
                    sb.Append("<li style='margin-bottom:8px'>");
                    sb.Append($"<div>{WebUtility.HtmlEncode(en)}</div>");

                    if (!string.IsNullOrWhiteSpace(pt))
                        sb.Append($"<div style='color:#555'><i>{WebUtility.HtmlEncode(pt)}</i></div>");
                    else
                        sb.Append($"<div style='color:#999'><i>(tradução PT-BR indisponível)</i></div>");

                    sb.Append("</li>");
                }

                sb.Append("</ol>");
                sb.Append("</div>");
            }

            sb.Append("</li>");
        }

        sb.Append("</ol>");
        sb.Append("<p style='color:#666'>Enviado automaticamente.</p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    // =========================
    // Email
    // =========================
    private static void SendEmail(string host, int port, string user, string pass, string from, string to, string subject, string htmlBody, string? audioPath)
    {
        using var msg = new MailMessage(from, to)
        {
            Subject = subject,
            IsBodyHtml = true,
            Body = htmlBody
        };

        if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
        {
            const string wavMime = "audio/wav";
            var att = new Attachment(audioPath, wavMime);
            att.Name = Path.GetFileName(audioPath);
            msg.Attachments.Add(att);
        }

        using var smtp = new SmtpClient(host, port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(user, pass)
        };

        smtp.Send(msg);
    }

    // =========================
    // Estado (sent_state.json)
    // =========================
    private sealed class SentState
    {
        [JsonPropertyName("sent")]
        public HashSet<string> Sent { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("blocked")]
        public Dictionary<string, BlockedEntry> Blocked { get; set; } = new Dictionary<string, BlockedEntry>(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("lastSentDate")]
        public string? LastSentDate { get; set; }

        [JsonPropertyName("lastRunIso")]
        public string? LastRunIso { get; set; }

        [JsonPropertyName("sendIteration")]
        public int SendIteration { get; set; } = 0;

        [JsonPropertyName("current")]
        public DispatchRun? Current { get; set; }
    }

    private sealed class DispatchRun
    {
        [JsonPropertyName("date")]
        public string Date { get; set; } = "";

        [JsonPropertyName("iteration")]
        public int Iteration { get; set; }

        [JsonPropertyName("pickRaw")]
        public List<string> PickRaw { get; set; } = new();

        [JsonPropertyName("analysis")]
        public DailyAnalysis? Analysis { get; set; }

        [JsonPropertyName("emailSubject")]
        public string? EmailSubject { get; set; }

        [JsonPropertyName("emailHtml")]
        public string? EmailHtml { get; set; }

        [JsonPropertyName("emailSent")]
        public bool EmailSent { get; set; }

        [JsonPropertyName("createdIso")]
        public string? CreatedIso { get; set; }

        [JsonPropertyName("payloadIso")]
        public string? PayloadIso { get; set; }

        [JsonPropertyName("lastEmailIso")]
        public string? LastEmailIso { get; set; }

        [JsonPropertyName("audioPath")]
        public string? AudioPath { get; set; }

        [JsonPropertyName("audioCreatedIso")]
        public string? AudioCreatedIso { get; set; }
    }

    private sealed class BlockedEntry
    {
        [JsonPropertyName("word")]
        public string Word { get; set; } = "";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        [JsonPropertyName("firstIso")]
        public string? FirstIso { get; set; }

        [JsonPropertyName("lastIso")]
        public string? LastIso { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }
    }


    private static SentState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SentState();
            var json = File.ReadAllText(path, Encoding.UTF8);

            var st = JsonSerializer.Deserialize<SentState>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new SentState();

            st.Sent ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            st.Blocked ??= new Dictionary<string, BlockedEntry>(StringComparer.OrdinalIgnoreCase);

            return st;
        }
        catch
        {
            return new SentState();
        }
    }

    private static void SaveState(string path, SentState state)
    {
        EnsureParentDirectory(path);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    // =========================
    // Modelos (JSON do Groq)
    // =========================
    private sealed class DailyAnalysis
    {
        [JsonPropertyName("items")]
        public List<DailyItem>? Items { get; set; }
    }

    private sealed class DailyItem
    {
        [JsonPropertyName("input")]
        public string Input { get; set; } = "";

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("verbForms")]
        public VerbForms? VerbForms { get; set; }

        [JsonPropertyName("translations")]
        public DailyTranslations? Translations { get; set; }

        [JsonPropertyName("examplesEn")]
        public List<string>? ExamplesEn { get; set; }

        [JsonPropertyName("examplesPt")]
        public List<string>? ExamplesPt { get; set; }
    }

    private sealed class VerbForms
    {
        [JsonPropertyName("present")]
        public string Present { get; set; } = "";

        [JsonPropertyName("pastSimple")]
        public string PastSimple { get; set; } = "";

        [JsonPropertyName("pastParticiple")]
        public string PastParticiple { get; set; } = "";
    }

    private sealed class DailyTranslations
    {
        [JsonPropertyName("general")]
        public List<string>? General { get; set; }

        [JsonPropertyName("present")]
        public List<string>? Present { get; set; }

        [JsonPropertyName("pastSimple")]
        public List<string>? PastSimple { get; set; }

        [JsonPropertyName("pastParticiple")]
        public List<string>? PastParticiple { get; set; }
    }

    // =========================
    // Vocabulário (.txt)
    // =========================
    private static List<string> LoadVocabularyLines(string path)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("#")) continue;

            var key = Norm(line);
            if (seen.Add(key))
                result.Add(line);
        }

        return result;
    }

    private static List<string> PickRandomLines(List<string> allLines, ISet<string> sent, Dictionary<string, BlockedEntry> blocked, int count)
    {
        var remaining = allLines
            .Where(x => !sent.Contains(Norm(x)))
            .Where(x => !blocked.ContainsKey(Norm(x)))
            .ToList();

        var pick = new List<string>(count);

        while (pick.Count < count && remaining.Count > 0)
        {
            int idx = RandomNumberGenerator.GetInt32(remaining.Count);
            pick.Add(remaining[idx]);
            remaining.RemoveAt(idx);
        }

        return pick;
    }

    // =========================
    // Retry
    // =========================
    private static async Task Retry(Func<Task> action)
    {
        Exception? last = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                var delayMs = (int)Math.Min(15 * 60_000, 1000 * Math.Pow(2, attempt - 1));
                Console.WriteLine($"Falhou (tentativa {attempt}/{MaxRetries}): {ex.Message}");
                Console.WriteLine($"Aguardando {delayMs / 1000}s e tentando de novo...");
                await Task.Delay(delayMs);
            }
        }

        throw new Exception("Falhou após retries.", last);
    }

    // =========================
    // Utils
    // =========================
    private static string Env(string name, bool required)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (required && string.IsNullOrWhiteSpace(v))
            throw new Exception($"Variável de ambiente faltando: {name}");
        return v ?? "";
    }

    private static bool EnvBool(string name, bool defaultValue)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return defaultValue;

        v = v.Trim();
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v.Equals("y", StringComparison.OrdinalIgnoreCase)
            || v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

    private static void TryDeleteAudioFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            // Não derruba a execução por causa disso
            Console.WriteLine($"Aviso: não consegui excluir o áudio '{path}': {ex.Message}");
        }
    }
}