using HtmlAgilityPack;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Data;

namespace AutoDescriptionLocalizer
{
    class ExceptionRule
    {
        public string Pattern { get; set; }
        public string Translation { get; set; }
        public int Priority { get; set; } = 0;
        public string Note { get; set; }
    }

    class ExceptionsConfig
    {
        public Dictionary<string, string> Fallbacks { get; set; } = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        public List<ExceptionRule> Overrides { get; set; } = new List<ExceptionRule>();
    }

    public class AutoDescriptionLocalizerPlugin : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly IPlayniteAPI playniteAPI;
        private readonly HashSet<Guid> currentlyTranslating = new HashSet<Guid>();
        private AutoDescriptionLocalizerSettingsViewModel settingsViewModel { get; set; }

        private readonly List<string> supportedLanguages = new List<string> { "en", "pt-BR", "es", "de", "fr", "ru" };
        private HashSet<string> cachedGameTitles = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        private HashSet<string> customTitlesFromTxt = new HashSet<string>();
        private string customTitlesFilePath;
        private FileSystemWatcher customTitlesWatcher;
        private System.Timers.Timer customTitlesReloadTimer;
        private TranslationQueue translationQueue = new TranslationQueue(maxConcurrentTranslations: 3);

        private ExceptionsConfig exceptionsConfig = new ExceptionsConfig();
        private readonly object exceptionsLock = new object();
        private string exceptionsFilePath;
        private FileSystemWatcher exceptionsWatcher;
        private System.Timers.Timer exceptionsReloadTimer;

        private Dictionary<string, string> replacements = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        private readonly object replacementsLock = new object();
        private string replacementsFilePath;
        private FileSystemWatcher replacementsWatcher;
        private System.Timers.Timer replacementsReloadTimer;

        private const string TranslationProgressNotificationId = "AutoDescriptionLocalizer_TranslationProgress";


        // Put your API link here before compiling
        public static string BaseUrl = "YOUR_API";

        public class TooManyRequestsException : Exception
        {
            public TooManyRequestsException(string message) : base(message) { }
        }

        public class TranslationQueue
        {
            private readonly SemaphoreSlim semaphore;
            private readonly Queue<Func<Task>> tasksQueue = new Queue<Func<Task>>();
            private readonly object lockObj = new object();
            private readonly int maxQueueSize;


            public TranslationQueue(int maxConcurrentTranslations, int maxQueueSize = 100)
            {
                semaphore = new SemaphoreSlim(maxConcurrentTranslations, maxConcurrentTranslations);
                this.maxQueueSize = maxQueueSize;
            }

            public void Enqueue(Func<Task> translationTask)
            {
                lock (lockObj)
                {
                    if (tasksQueue.Count >= maxQueueSize) return;
                    tasksQueue.Enqueue(translationTask);
                }
                _ = ProcessQueue();
            }

            private async Task ProcessQueue()
            {
                Func<Task> task = null;
                lock (lockObj)
                {
                    if (tasksQueue.Count > 0)
                        task = tasksQueue.Dequeue();
                }

                if (task == null) return;

                await semaphore.WaitAsync();
                try { await task(); }
                catch (Exception ex) { logger.Error(ex, "[AutoDescriptionLocalizer] Erro ao processar tradução da fila."); }
                finally
                {
                    semaphore.Release();
                    _ = ProcessQueue();
                }
            }
        }

        private (string preparedText, Func<string, string> restoreFunction) PrepareAndRestoreTextForTranslation(string text, Regex protectionRegex)
        {
            var protectedTerms = new Dictionary<string, string>();
            var sb = new StringBuilder();
            int lastIndex = 0;
            int placeholderCounter = 1;

            var matches = protectionRegex.Matches(text).Cast<Match>().OrderBy(m => m.Index).ToList();

            foreach (var match in matches)
            {
                if (match.Index > lastIndex)
                {
                    sb.Append(text.Substring(lastIndex, match.Index - lastIndex));
                }

                var originalTerm = match.Value;

                var placeholder = $"PH{placeholderCounter}";

                if (!string.IsNullOrEmpty(originalTerm) && !string.IsNullOrEmpty(placeholder))
                {
                    protectedTerms[placeholder] = originalTerm;
                    sb.Append(placeholder);
                    placeholderCounter++;
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                sb.Append(text.Substring(lastIndex));
            }

            return (sb.ToString(), (translatedText) =>
            {
                var restoredText = translatedText;
                foreach (var kvp in protectedTerms.OrderByDescending(x => x.Key.Length))
                {
                    restoredText = restoredText.Replace(kvp.Key, kvp.Value);
                }
                return restoredText;
            }
            );
        }
        public override Guid Id { get; } = Guid.Parse("FD25D279-1A13-4028-8438-E468110B28A6");

        public AutoDescriptionLocalizerPlugin(IPlayniteAPI api) : base(api)
        {
            playniteAPI = api;
            Properties = new GenericPluginProperties { HasSettings = true };

            var savedSettings = LoadPluginSettings<AutoDescriptionLocalizerSettings>() ?? new AutoDescriptionLocalizerSettings();
            settingsViewModel = new AutoDescriptionLocalizerSettingsViewModel(this, savedSettings, playniteAPI.ApplicationSettings.Language);

            playniteAPI.Database.Games.ItemUpdated += OnGameUpdated;
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            var pluginDir = Path.GetDirectoryName(this.GetType().Assembly.Location) ?? ".";
            customTitlesFilePath = Path.Combine(pluginDir, "gamelist.txt");
            exceptionsFilePath = Path.Combine(pluginDir, "exceptions-pre-translation-rules.json");
            replacementsFilePath = Path.Combine(pluginDir, "replacements.json");

            LoadCustomTitlesFromTxt();
            SetupCustomTitlesFileWatcher();
            LoadExceptionsConfig();
            SetupExceptionsFileWatcher();
            LoadReplacementsConfig();
            SetupReplacementsFileWatcher();
        }


        public override ISettings GetSettings(bool firstRunSettings) => settingsViewModel;
        public override UserControl GetSettingsView(bool firstRunSettings) => new AutoDescriptionLocalizerSettingsView { DataContext = settingsViewModel };

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);
            _ = PingApiOnStartup();

            logger.Info("[AutoDescriptionLocalizer] Carregando nomes de jogos da biblioteca para a lista de proteção...");
            cachedGameTitles = playniteAPI.Database.Games
                .Select(g => RemoveTrademarkSymbols(g.Name).Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            logger.Info($"[AutoDescriptionLocalizer] {cachedGameTitles.Count} nomes de jogos carregados.");
        }
        private async Task<string> DetectLanguageAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            if (text.Length > 1000)
                text = text.Substring(0, 1000);

            try
            {
                var detectUrl = $"{BaseUrl}/detect";
                var body = new { q = text };
                var content = new StringContent(Serialization.ToJson(body), Encoding.UTF8, "application/json");

                var resp = await httpClient.PostAsync(detectUrl, content, ct);
                var respBody = await resp.Content.ReadAsStringAsync();

                if ((int)resp.StatusCode == 429)
                {
                    throw new TooManyRequestsException("O limite de requisições foi excedido.");
                }
                else if (!resp.IsSuccessStatusCode)
                {
                    logger.Warn($"[AutoDescriptionLocalizer] /detect retornou {resp.StatusCode}. Resposta: {respBody}");
                    return null;
                }

                dynamic parsed = Serialization.FromJson<dynamic>(respBody);

                try
                {
                    if (parsed is System.Collections.IEnumerable && parsed.Count > 0 && parsed[0].language != null)
                    {
                        return (string)parsed[0].language;
                    }
                }
                catch { }

                try
                {
                    if (parsed != null && parsed.language != null)
                    {
                        return (string)parsed.language;
                    }
                }
                catch { }

                logger.Warn("[AutoDescriptionLocalizer] /detect respondeu formato inesperado.");
                return null;
            }
            catch (TooManyRequestsException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.Warn("[AutoDescriptionLocalizer] /detect cancelado por timeout.");
                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] Exceção ao chamar /detect.");
                return null;
            }
        }


        private void OnGameUpdated(object sender, ItemUpdatedEventArgs<Game> args)
        {
            foreach (var update in args.UpdatedItems)
            {
                var newGame = update.NewData;
                if (string.IsNullOrWhiteSpace(newGame.Description) || currentlyTranslating.Contains(newGame.Id)) continue;
                logger.Info($"[AutoDescriptionLocalizer] Detectada mudança na descrição de '{newGame.Name}'.");
                translationQueue.Enqueue(() => TranslateDescription(newGame));
            }
        }

        private string FixCapitalization(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            text = Regex.Replace(text, @"^(\s*[a-z])", m => m.Value.ToUpper());

            text = Regex.Replace(text, @"([\.!\?]\s+)([a-z])",
                m => m.Groups[1].Value + m.Groups[2].Value.ToUpper());

            return text;
        }
        private void AddTemporaryNotification(string id, string message, NotificationType type, int seconds = 15)
        {
            var notification = new NotificationMessage(id, message, type);
            playniteAPI.Notifications.Add(notification);

            Task.Run(async () =>
            {
                await Task.Delay(seconds * 1000);
                playniteAPI.Notifications.Remove(id);
            });
        }

        private async Task TranslateDescription(Game game)
        {
            if (currentlyTranslating.Contains(game.Id)) return;
            currentlyTranslating.Add(game.Id);

            var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(settingsViewModel.Settings.Timeout));
            bool isInterfaceInPortuguese = false;
            ProgressOverlay overlay = null;

            var targetLang = settingsViewModel.Settings.TargetLanguage;
            if (string.IsNullOrEmpty(targetLang))
                targetLang = playniteAPI.ApplicationSettings.Language;

            isInterfaceInPortuguese = (targetLang ?? "").StartsWith("pt", StringComparison.OrdinalIgnoreCase);
            var normalizedTargetLang = isInterfaceInPortuguese ? "pt-BR" : targetLang.Split(new char[] { '-', '_' })[0];
            if (!supportedLanguages.Contains(normalizedTargetLang))
                normalizedTargetLang = "en";

            string notificationStartMessage = isInterfaceInPortuguese ?
                $"Iniciando tradução para: {game.Name}" :
                $"Starting translation for: {game.Name}";

            AddTemporaryNotification(Guid.NewGuid().ToString(), notificationStartMessage, NotificationType.Info, 5);


            try
            {
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(game.Description);
                var mainText = htmlDoc.DocumentNode.InnerText.Trim();

                if (string.IsNullOrWhiteSpace(mainText))
                {
                    currentlyTranslating.Remove(game.Id);
                    playniteAPI.Notifications.Remove(TranslationProgressNotificationId); 
                    string noTextNotification = isInterfaceInPortuguese ?
                        $"Nenhuma descrição encontrada para traduzir: {game.Name}" :
                        $"No description found to translate for: {game.Name}";

                    AddTemporaryNotification(Guid.NewGuid().ToString(), noTextNotification, NotificationType.Info, 5);

                    return;
                }

                bool needsTranslation = true;
                string sampleText = RemoveTrademarkSymbols(mainText);

                if (!string.IsNullOrEmpty(sampleText))
                {
                    var detectedLang = await DetectLanguageAsync(sampleText, overallCts.Token);

                    var targetPrimary = (normalizedTargetLang ?? "en").Split('-')[0];
                    if (!string.IsNullOrEmpty(detectedLang))
                    {
                        needsTranslation = !string.Equals(detectedLang, targetPrimary, StringComparison.OrdinalIgnoreCase);
                        logger.Info($"[LANG_DETECT] Jogo '{game.Name}': detectado='{detectedLang}', alvo='{targetPrimary}', traduzir?={needsTranslation}");
                    }
                    else
                    {
                        logger.Warn($"[LANG_DETECT] Não foi possível detectar idioma para '{game.Name}'. Prosseguindo com tradução.");
                    }
                }

                if (!needsTranslation)
                {
                    currentlyTranslating.Remove(game.Id);
                    return;
                }

                string overlayText = isInterfaceInPortuguese ? "Traduzindo" : "Translating";
                overlay = ShowProgress($"{overlayText}: {TruncateForOverlay(game.Name, 60)}");

                string progressMessage = isInterfaceInPortuguese ?
                    $"Traduzindo descrição de {game.Name}..." :
                    $"Translating description for {game.Name}... (0%)";
                playniteAPI.Notifications.Remove(TranslationProgressNotificationId);

                AddTemporaryNotification(Guid.NewGuid().ToString(), progressMessage, NotificationType.Info, 5);


                var allTitlesForProtection = new HashSet<string>(
                    cachedGameTitles.Concat(customTitlesFromTxt.Select(t => t.Trim())),
                    StringComparer.OrdinalIgnoreCase
                );

                var longTitles = allTitlesForProtection
                    .Where(t => !string.IsNullOrWhiteSpace(t) && t.Count(c => c == ' ') > 0)
                    .OrderByDescending(t => t.Length)
                    .Select(Regex.Escape)
                    .ToList();

                var shortTitles = allTitlesForProtection
                    .Where(t => !string.IsNullOrWhiteSpace(t) && t.Count(c => c == ' ') <= 0 && t.Any(c => Char.IsUpper(c)))
                    .OrderByDescending(t => t.Length)
                    .Select(Regex.Escape)
                    .ToList();

                var patternParts = new List<string>();

                if (longTitles.Any())
                {
                    patternParts.Add($"({string.Join("|", longTitles)})");
                }

                if (shortTitles.Any())
                {
                    patternParts.Add($@"(?<!^|[\.\?\!]\s)(?-i)\b({string.Join("|", shortTitles)})\b");
                }

                var masterPattern = patternParts.Any() ? string.Join("|", patternParts) : "(?!)";
                var masterRegex = new Regex(masterPattern, RegexOptions.IgnoreCase);


                string textAfterExceptions = mainText;
                if (normalizedTargetLang == "pt-BR")
                {
                    textAfterExceptions = ApplyExceptions(mainText);
                }
                var (preparedText, restoreFunction) = PrepareAndRestoreTextForTranslation(textAfterExceptions, masterRegex);

                const int MAX_CHARS_PER_BLOCK = 2000;
                var blocks = new List<string>();
                for (int i = 0; i < preparedText.Length; i += MAX_CHARS_PER_BLOCK)
                {
                    blocks.Add(preparedText.Substring(i, Math.Min(MAX_CHARS_PER_BLOCK, preparedText.Length - i)));
                }

                var translatedBlocks = new List<string>();
                for (int i = 0; i < blocks.Count; i++)
                {
                    var block = blocks[i];
                    string translatedBlock = await CallTranslateApi(block, normalizedTargetLang, overallCts.Token);
                    translatedBlocks.Add(translatedBlock);

                    double currentPercent = ((i + 1) / (double)blocks.Count) * 100.0;
                    string currentStatus = isInterfaceInPortuguese ?
                        $"Traduzindo: {TruncateForOverlay(game.Name, 60)} ({currentPercent:F0}%)" :
                        $"Translating: {TruncateForOverlay(game.Name, 60)} ({currentPercent:F0}%)";

                    UpdateProgress(overlay, currentPercent, currentStatus);

                    string notificationProgress = isInterfaceInPortuguese ?
                        $"Traduzindo descrição de {game.Name}... ({currentPercent:F0}%)" :
                        $"Translating description for {game.Name}... ({currentPercent:F0}%)";
                    playniteAPI.Notifications.Remove(TranslationProgressNotificationId);

                    AddTemporaryNotification(Guid.NewGuid().ToString(), notificationProgress, NotificationType.Info, 5);

                }
                var translatedText = string.Join("", translatedBlocks);

                if (normalizedTargetLang == "pt-BR")
                {
                    logger.Debug("[AutoDescriptionLocalizer] Aplicando replacements após a tradução.");
                    translatedText = ApplyReplacements(translatedText);
                    translatedText = NormalizeSpacingAfterReplacement(translatedText);
                    translatedText = FixCapitalization(translatedText);
                }
                var finalTranslatedText = restoreFunction(translatedText);

                var originalHtmlDoc = new HtmlDocument();
                originalHtmlDoc.LoadHtml(game.Description);
                var textNode = originalHtmlDoc.DocumentNode.DescendantsAndSelf()
                    .FirstOrDefault(n => n.NodeType == HtmlNodeType.Text && n.InnerText.Trim() == mainText);

                if (textNode != null)
                {
                    textNode.InnerHtml = finalTranslatedText;
                }
                else
                {
                    originalHtmlDoc.DocumentNode.InnerHtml = finalTranslatedText;
                }

                var newDescription = originalHtmlDoc.DocumentNode.OuterHtml;

                bool shouldUpdate = !settingsViewModel.Settings.AskBeforeOverwrite;
                if (settingsViewModel.Settings.AskBeforeOverwrite)
                {
                    string title = isInterfaceInPortuguese ? "Confirmar Tradução" : "Confirm Translation";
                    string message = isInterfaceInPortuguese
                        ? $"Uma nova descrição traduzida foi gerada. Deseja substituir a descrição atual?"
                        : "A new translated description was generated. Do you want to replace the current description?";
                    shouldUpdate = playniteAPI.Dialogs.ShowMessage(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                }

                if (shouldUpdate)
                {
                    var gameToUpdate = playniteAPI.Database.Games.Get(game.Id);
                    if (gameToUpdate != null)
                    {
                        gameToUpdate.Description = newDescription;
                        playniteAPI.Database.Games.Update(gameToUpdate);
                    }
                }

                UpdateProgress(overlay, 100, isInterfaceInPortuguese ? "Concluído" : "Completed");
                await Task.Delay(500);

                playniteAPI.Notifications.Remove(TranslationProgressNotificationId); 
                string completionNotification = isInterfaceInPortuguese ?
                    $"Tradução de '{game.Name}' concluída com sucesso!" :
                    $"Translation for '{game.Name}' completed successfully!";

                AddTemporaryNotification(Guid.NewGuid().ToString(), completionNotification, NotificationType.Info, 15);

            }
            catch (TooManyRequestsException ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] O limite de requisições foi excedido. Tradução cancelada.");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    string title = isInterfaceInPortuguese ? "Limite de Requisições Excedido" : "Request Limit Exceeded";
                    string message = isInterfaceInPortuguese
                        ? $"O limite de requisições foi excedido. A tradução de '{game.Name}' foi interrompida. Tente novamente mais tarde."
                        : $"The request limit has been exceeded. Translation of '{game.Name}' was cancelled. Please try again later.";
                    playniteAPI.Dialogs.ShowMessage(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
                });

                playniteAPI.Notifications.Remove(TranslationProgressNotificationId);
                string errorNotification = isInterfaceInPortuguese ?
                    $"Erro na tradução de '{game.Name}': Limite de requisições excedido." :
                    $"Translation error for '{game.Name}': Request limit exceeded.";

                AddTemporaryNotification(Guid.NewGuid().ToString(), errorNotification, NotificationType.Info, 5);

            }
            catch (OperationCanceledException)
            {
                logger.Warn($"[AutoDescriptionLocalizer] A tradução de '{game.Name}' foi cancelada por timeout.");
                playniteAPI.Notifications.Remove(TranslationProgressNotificationId);
                string cancelledNotification = isInterfaceInPortuguese ?
                    $"Tradução de '{game.Name}' cancelada por timeout." :
                    $"Translation for '{game.Name}' cancelled due to timeout.";

                AddTemporaryNotification(Guid.NewGuid().ToString(), cancelledNotification, NotificationType.Info, 5);

            }
            catch (Exception ex)
            {
                logger.Error(ex, $"[AutoDescriptionLocalizer] Falha ao traduzir '{game.Name}'.");
                playniteAPI.Notifications.Remove(TranslationProgressNotificationId);
                string failureNotification = isInterfaceInPortuguese ?
                    $"Falha na tradução de '{game.Name}'. Verifique os logs." :
                    $"Translation failed for '{game.Name}'. Check logs.";

                AddTemporaryNotification(Guid.NewGuid().ToString(), failureNotification, NotificationType.Info, 5);

            }
            finally
            {
                if (overlay != null)
                {
                    HideProgress(overlay);
                }

                currentlyTranslating.Remove(game.Id);

                playniteAPI.Notifications.Remove(TranslationProgressNotificationId);
            }
        }


        private async Task<string> TranslateTextWithLimits(string text, string targetLang, CancellationToken ct)
        {
            const int MAX_CHARS_PER_BLOCK = 2000;
            var blocks = new List<string>();
            for (int i = 0; i < text.Length; i += MAX_CHARS_PER_BLOCK)
            {
                blocks.Add(text.Substring(i, Math.Min(MAX_CHARS_PER_BLOCK, text.Length - i)));
            }
            var sb = new StringBuilder();
            foreach (var block in blocks)
            {
                sb.Append(await CallTranslateApi(block, targetLang, ct));
            }
            return sb.ToString();
        }

        private async Task<string> CallTranslateApi(string text, string targetLang, CancellationToken ct)
        {
            string apiUrl = $"{BaseUrl}/translate";

            var requestBody = new { q = text, source = "auto", target = targetLang, format = "text" };
            var content = new StringContent(Serialization.ToJson(requestBody), Encoding.UTF8, "application/json");
            try
            {
                HttpResponseMessage response = await httpClient.PostAsync(apiUrl, content, ct);
                string responseBody = await response.Content.ReadAsStringAsync();

                if ((int)response.StatusCode == 429)
                {
                    throw new TooManyRequestsException("O limite de requisições foi excedido.");
                }
                else if (!response.IsSuccessStatusCode)
                {
                    logger.Warn($"[AutoDescriptionLocalizer] API de tradução retornou um erro: {response.StatusCode}. Resposta: {responseBody}");
                    return text; 
                }

                dynamic result = Serialization.FromJson<dynamic>(responseBody);
                string detectedLanguage = result.detectedLanguage?.language;
                string translatedText = result.translatedText;

                logger.Info($"[API_DEBUG] ENVIADO: \"{text.Replace("\n", " ").Trim()}\"");
                logger.Info($"[API_DEBUG] RECEBIDO: \"{translatedText.Replace("\n", " ").Trim()}\"");

                if (!string.IsNullOrEmpty(detectedLanguage))
                {
                    var normalizedDetected = detectedLanguage.Split('-')[0];
                    var normalizedTarget = targetLang.Split('-')[0];

                    if (normalizedDetected.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.Info($"[AutoDescriptionLocalizer] Texto já está no idioma '{targetLang}', não traduzindo.");
                        return text;
                    }
                }
                return translatedText;
            }
            catch (TooManyRequestsException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"[AutoDescriptionLocalizer] Exceção ao chamar a API de tradução.");
                return text;
            }
        }

        private static readonly Dictionary<string, Regex> regexCache = new Dictionary<string, Regex>();
        private static readonly object regexCacheLock = new object();

        private static Regex GetOrCompileRegex(string pattern)
        {
            lock (regexCacheLock)
            {
                if (!regexCache.TryGetValue(pattern, out var rx))
                {
                    rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    regexCache[pattern] = rx;
                }
                return rx;
            }
        }

        private string ApplyExceptions(string text)
        {
            List<ExceptionRule> overridesCopy;
            lock (exceptionsLock)
            {
                overridesCopy = exceptionsConfig.Overrides.ToList();
            }

            foreach (var rule in overridesCopy.OrderByDescending(r => r.Priority))
            {
                try
                {
                    var rx = GetOrCompileRegex(rule.Pattern);
                    text = rx.Replace(text, rule.Translation ?? "");
                }
                catch (Exception innerEx)
                {
                    logger.Error(innerEx, $"[AutoDescriptionLocalizer] Erro aplicando override '{rule.Pattern}'");
                }
            }

            return text;
        }

        private string ApplyReplacements(string text)
        {
            KeyValuePair<string, string>[] replacementsCopy;
            lock (replacementsLock)
            {
                replacementsCopy = replacements.ToArray();
            }

            foreach (var kv in replacementsCopy.OrderByDescending(k => k.Key.Length))
            {
                try
                {
                    var rx = GetOrCompileRegex(Regex.Escape(kv.Key));
                    text = rx.Replace(text, kv.Value);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"[AutoDescriptionLocalizer] Falha ao aplicar replacement '{kv.Key}'");
                }
            }

            return text;
        }



        private string RemoveTrademarkSymbols(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"[™®©]", "", RegexOptions.IgnoreCase).Trim();
        }

        private string TruncateForOverlay(string s, int maxLen = 120)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen - 3) + "...";
        }

        private string NormalizeSpacingAfterReplacement(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = Regex.Replace(text, @"[ \t]{2,}", " "); 
            text = Regex.Replace(text, @"\s+([,;.!?%])", "$1"); 
            text = Regex.Replace(text, @"([,;.!?%])([^\s\p{P}])", "$1 $2"); 
            text = Regex.Replace(text, @"\s{2,}", " "); 
            return text.Trim();
        }

        private async Task PingApiOnStartup()
        {
            try
            {

                await httpClient.GetAsync($"{BaseUrl}/docs");
                logger.Info("[AutoDescriptionLocalizer] API 'acordada'.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] Falha no 'ping' para a API.");
            }
        }

        #region File Watchers and Config Loaders

        private void LoadCustomTitlesFromTxt()
        {
            try
            {
                if (!File.Exists(customTitlesFilePath))
                {
                    File.WriteAllText(customTitlesFilePath, "", Encoding.UTF8);
                    customTitlesFromTxt = new HashSet<string>(StringComparer.Ordinal);
                    return;
                }

                customTitlesFromTxt = new HashSet<string>(
                    File.ReadLines(customTitlesFilePath, Encoding.UTF8)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => RemoveTrademarkSymbols(l.Trim())),
                    StringComparer.OrdinalIgnoreCase
                );

                logger.Info($"[AutoDescriptionLocalizer] {customTitlesFromTxt.Count} títulos customizados carregados.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao carregar gamelist.txt.");
                customTitlesFromTxt = new HashSet<string>(StringComparer.Ordinal);
            }
        }


        private void SetupCustomTitlesFileWatcher()
        {
            try
            {
                customTitlesWatcher = new FileSystemWatcher(Path.GetDirectoryName(customTitlesFilePath), Path.GetFileName(customTitlesFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                customTitlesWatcher.Changed += (s, e) => DebouncedReloadCustomTitles();
                customTitlesWatcher.Renamed += (s, e) => DebouncedReloadCustomTitles();
                customTitlesWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao configurar watcher para gamelist.txt."); }
        }

        private void DebouncedReloadCustomTitles()
        {
            customTitlesReloadTimer?.Stop();
            if (customTitlesReloadTimer == null)
            {
                customTitlesReloadTimer = new System.Timers.Timer(500) { AutoReset = false };
                customTitlesReloadTimer.Elapsed += (s, e) => LoadCustomTitlesFromTxt();
            }
            customTitlesReloadTimer.Start();
        }

        private void LoadExceptionsConfig()
        {
            try
            {
                lock (exceptionsLock)
                {
                    if (!File.Exists(exceptionsFilePath))
                    {
                        File.WriteAllText(exceptionsFilePath, Serialization.ToJson(new ExceptionsConfig(), true), Encoding.UTF8);
                        exceptionsConfig = new ExceptionsConfig();
                        return;
                    }
                    var json = File.ReadAllText(exceptionsFilePath, Encoding.UTF8);
                    exceptionsConfig = Serialization.FromJson<ExceptionsConfig>(json) ?? new ExceptionsConfig();
                    logger.Info($"[AutoDescriptionLocalizer] Exceptions carregadas ({exceptionsConfig.Overrides.Count} overrides, {exceptionsConfig.Fallbacks.Count} fallbacks).");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao carregar exceptions-pre-translation-rules.json");
                exceptionsConfig = new ExceptionsConfig();
            }
        }

        private void SetupExceptionsFileWatcher()
        {
            try
            {
                exceptionsWatcher = new FileSystemWatcher(Path.GetDirectoryName(exceptionsFilePath), Path.GetFileName(exceptionsFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                exceptionsWatcher.Changed += (s, e) => DebouncedReloadExceptions();
                exceptionsWatcher.Renamed += (s, e) => DebouncedReloadExceptions();
                exceptionsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao configurar watcher para exceptions-pre-translation-rules.json."); }
        }

        private void DebouncedReloadExceptions()
        {
            exceptionsReloadTimer?.Stop();
            if (exceptionsReloadTimer == null)
            {
                exceptionsReloadTimer = new System.Timers.Timer(500) { AutoReset = false };
                exceptionsReloadTimer.Elapsed += (s, e) => LoadExceptionsConfig();
            }
            exceptionsReloadTimer.Start();
        }

        private void LoadReplacementsConfig()
        {
            try
            {
                lock (replacementsLock)
                {
                    if (!File.Exists(replacementsFilePath))
                    {
                        File.WriteAllText(replacementsFilePath, Serialization.ToJson(new Dictionary<string, string>(), true), Encoding.UTF8);
                        replacements = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
                        return;
                    }
                    var json = File.ReadAllText(replacementsFilePath, Encoding.UTF8);
                    replacements = new Dictionary<string, string>(Serialization.FromJson<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(), StringComparer.InvariantCultureIgnoreCase);
                    logger.Info($"[AutoDescriptionLocalizer] Replacements carregados ({replacements.Count} entradas).");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao carregar replacements.json.");
                replacements = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            }
        }

        private void SetupReplacementsFileWatcher()
        {
            try
            {
                replacementsWatcher = new FileSystemWatcher(Path.GetDirectoryName(replacementsFilePath), Path.GetFileName(replacementsFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                replacementsWatcher.Changed += (s, e) => DebouncedReloadReplacements();
                replacementsWatcher.Renamed += (s, e) => DebouncedReloadReplacements();
                replacementsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao configurar watcher para replacements.json."); }
        }

        private void DebouncedReloadReplacements()
        {
            replacementsReloadTimer?.Stop();
            if (replacementsReloadTimer == null)
            {
                replacementsReloadTimer = new System.Timers.Timer(500) { AutoReset = false };
                replacementsReloadTimer.Elapsed += (s, e) => LoadReplacementsConfig();
            }
            replacementsReloadTimer.Start();
        }

        #endregion

        #region Progress Overlay

        private ProgressOverlay currentProgressOverlay;
        private readonly object overlayLock = new object(); 

        private ProgressOverlay ShowProgress(string initialText) 
        {
            lock (overlayLock) 
            {
                if (currentProgressOverlay != null && currentProgressOverlay.IsVisible)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        currentProgressOverlay.SetProgress(0, initialText);
                    });
                    return currentProgressOverlay;
                }

                ProgressOverlay newOverlay = null;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    newOverlay = new ProgressOverlay(Application.Current.MainWindow)
                    {
                        Width = 260,
                        Height = 48,
                        Topmost = false,
                        ShowInTaskbar = false,
                        Background = System.Windows.Media.Brushes.Transparent,
                        AllowsTransparency = true,
                        Opacity = 0,
                    };

                    newOverlay.Top = newOverlay.Owner.Top + 20;
                    newOverlay.Left = newOverlay.Owner.Left + newOverlay.Owner.Width - newOverlay.Width - 20;

                    newOverlay.SetProgress(0, initialText);
                    newOverlay.Owner = null; 
                    newOverlay.ShowActivated = false;
                    newOverlay.Show();

                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                    newOverlay.BeginAnimation(Window.OpacityProperty, fadeIn);

                    currentProgressOverlay = newOverlay; 
                });
                return newOverlay;
            }
        }



        private void UpdateProgress(ProgressOverlay overlay, double percent, string status) 
        {
            if (overlay == null || overlay != currentProgressOverlay) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                overlay.SetProgress(percent, status);
            });
        }


        private void HideProgress(ProgressOverlay overlay)
        {
            if (overlay == null || overlay != currentProgressOverlay) return; 

            lock (overlayLock)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (overlay.IsVisible)
                    {
                        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(overlay.Opacity, 0, TimeSpan.FromMilliseconds(300));
                        fadeOut.Completed += (s, e) =>
                        {
                            try { overlay.Close(); } catch { }
                            currentProgressOverlay = null; 
                        };
                        overlay.BeginAnimation(Window.OpacityProperty, fadeOut);
                    }
                    else
                    {
                        currentProgressOverlay = null; 
                    }
                });
            }
        }



        #endregion
    }
}