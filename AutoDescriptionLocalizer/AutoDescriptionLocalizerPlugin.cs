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

        private readonly List<string> supportedLanguages = new List<string> { "en", "pt-BR", "es", "de", "fr", "ru", "ja" };
        private HashSet<string> cachedGameTitles = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        private HashSet<string> customTitlesFromTxt = new HashSet<string>();
        private string customTitlesFilePath;
        private FileSystemWatcher customTitlesWatcher;
        private System.Timers.Timer customTitlesReloadTimer;
        private static readonly Random random = new Random();
        private bool largeQueueNotified = false;
        private int gamesInQueue = 0;

        private TranslationQueue translationQueue = new TranslationQueue(maxConcurrentTranslations: 1, maxQueueSize: 50000);

        private ExceptionsConfig exceptionsConfig = new ExceptionsConfig();
        private readonly object exceptionsLock = new object();
        private string exceptionsFilePath;
        private FileSystemWatcher exceptionsWatcher;
        private System.Timers.Timer exceptionsReloadTimer;

        private Dictionary<string, Dictionary<string, string>> replacements = new Dictionary<string, Dictionary<string, string>>(StringComparer.InvariantCultureIgnoreCase);
        private readonly object replacementsLock = new object();
        private string replacementsFilePath;
        private FileSystemWatcher replacementsWatcher;
        private System.Timers.Timer replacementsReloadTimer;

        private const string TranslationProgressNotificationId = "AutoDescriptionLocalizer_TranslationProgress";

        private TranslationCache translationCache = new TranslationCache();
        private readonly object translationCacheLock = new object();
        private string translationCacheFilePath;
        private FileSystemWatcher translationCacheWatcher;
        private System.Timers.Timer translationCacheReloadTimer;
        private DateTime lastRateLimitHit = DateTime.MinValue;
        private int currentCooldownMinutes = 5;
        private bool IsInCooldown => (DateTime.Now - lastRateLimitHit).TotalMinutes < currentCooldownMinutes;




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
            var wordToPlaceholder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            int lastIndex = 0;
            int placeholderCounter = 1;

            var matches = protectionRegex.Matches(text).Cast<Match>().OrderBy(m => m.Index).ToList();

            foreach (var match in matches)
            {
                if (match.Index > lastIndex) sb.Append(text.Substring(lastIndex, match.Index - lastIndex));

                var originalTerm = match.Value.Trim();
                if (!wordToPlaceholder.TryGetValue(originalTerm, out var placeholder))
                {
                    placeholder = $"PH{placeholderCounter++}";
                    wordToPlaceholder[originalTerm] = placeholder;
                    protectedTerms[placeholder] = originalTerm;
                }

                sb.Append($" {placeholder} ");
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length) sb.Append(text.Substring(lastIndex));

            return (sb.ToString(), (translatedText) =>
            {
                var restoredText = translatedText;
           
                foreach (var kvp in protectedTerms.OrderByDescending(x => x.Key.Length))
                {
                  
                    restoredText = Regex.Replace(restoredText, Regex.Escape(kvp.Key), kvp.Value, RegexOptions.IgnoreCase);
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
            translationCacheFilePath = Path.Combine(pluginDir, "translation_cache.json");

            LoadCustomTitlesFromTxt();
            SetupCustomTitlesFileWatcher();
            LoadExceptionsConfig();
            SetupExceptionsFileWatcher();
            LoadReplacementsConfig();
            SetupReplacementsFileWatcher();
            LoadTranslationCache(); 
            SetupTranslationCacheFileWatcher();
        }


        public override ISettings GetSettings(bool firstRunSettings) => settingsViewModel;
        public override UserControl GetSettingsView(bool firstRunSettings) => new AutoDescriptionLocalizerSettingsView { DataContext = settingsViewModel };

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);

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

                Interlocked.Increment(ref gamesInQueue);

             
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


        private double CalculateSimilarity(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            {
                return 0.0;
            }


            source = new string(source.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLower();
            target = new string(target.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLower();

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            {
                return 0.0;
            }

            var sourceSet = new HashSet<char>(source);
            var targetSet = new HashSet<char>(target);


            double intersectionCount = sourceSet.Intersect(targetSet).Count();
            double unionCount = sourceSet.Union(targetSet).Count();

            if (unionCount == 0) return 0.0;
            return intersectionCount / unionCount;
        }
        private (string preparedText, Func<string, string> restoreFunction) PrepareAndRestoreHtmlForTranslation(string htmlDescription)
        {
            var protectionRegex = new Regex("(<[^>]+>)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var protectedTerms = new Dictionary<string, string>();
            var sb = new StringBuilder();
            int lastIndex = 0;
            int placeholderCounter = 1;

            var text = htmlDescription;

            var matches = protectionRegex.Matches(text).Cast<Match>().OrderBy(m => m.Index).ToList();

            foreach (var match in matches)
            {
                if (match.Index > lastIndex)
                {
                    
                    sb.Append(text.Substring(lastIndex, match.Index - lastIndex));
                }

                var originalTerm = match.Value;
                var placeholder = $"HT{placeholderCounter}";

                if (!string.IsNullOrEmpty(originalTerm))
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

   
            var preparedText = sb.ToString();

            return (preparedText, (translatedText) =>
            {
                var restoredText = translatedText;

                foreach (var kvp in protectedTerms.OrderByDescending(x => x.Key.Length).ThenByDescending(x => x.Key))
                {

                    var pattern = Regex.Escape(kvp.Key);
                    restoredText = Regex.Replace(restoredText, pattern, kvp.Value, RegexOptions.IgnoreCase);
                }

                restoredText = Regex.Replace(restoredText, @"(</\w+>)([a-zA-Z0-9À-ÿ])", "$1 $2");

                return restoredText;
            }
            );
        }

        public void ClearTranslationCache()
        {
            lock (translationCacheLock)
            {
              
                translationCache = new TranslationCache();
                translationCache.Cache = new Dictionary<string, List<CachedTranslationEntry>>();

                try
                {
                    
                    if (File.Exists(translationCacheFilePath))
                    {
                        File.Delete(translationCacheFilePath);
                    }
                   
                    SaveTranslationCache();
                    logger.Info("[AutoDescriptionLocalizer] Cache de tradução resetado com sucesso.");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "[AutoDescriptionLocalizer] Erro ao limpar arquivo de cache.");
                }
            }
        }

        private bool ContainsHtmlTags(string text)
        {
         
            return Regex.IsMatch(text, @"<[^>]+>", RegexOptions.Compiled);
        }
        private Regex GetListProtectionRegex()
        {
        
            var allTitles = customTitlesFromTxt.Concat(cachedGameTitles)
                .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length > 2)
                .OrderByDescending(t => t.Length)
                .Select(Regex.Escape);

            if (!allTitles.Any()) return null;

            return new Regex($@"\b({string.Join("|", allTitles)})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        private DateTime lastTranslationTime = DateTime.MinValue;

        private DateTime lastTranslationEndTime = DateTime.MinValue;

        private const string QueueNotificationId = "ADL_QueueProgress";
        private async Task<string> TranslateTextWithLimits(string text, string targetLang, CancellationToken ct, string gameName)
        {
            
            if (text.Length <= 5000)
            {
                return await CallTranslateApi(text, targetLang, ct);
            }

            
            const int CHUNK_SIZE = 2500;
            var sb = new StringBuilder();
            int offset = 0;
            const string LongTextNotifId = "ADL_LongText";

          
            bool isPt = playniteAPI.ApplicationSettings.Language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
            string msg = isPt
                ? $"Texto extenso em '{gameName}'. Traduzindo por partes..."
                : $"Long text in '{gameName}'. Translating in chunks...";

            playniteAPI.Notifications.Add(new NotificationMessage(LongTextNotifId, msg, NotificationType.Info));
            logger.Info($"[AutoDescriptionLocalizer] Texto gigante ({text.Length} chars) em '{gameName}'. Iniciando modo em blocos.");

            try
            {
                while (offset < text.Length)
                {
                    int length = Math.Min(CHUNK_SIZE, text.Length - offset);

                    
                    if (offset + length < text.Length)
                    {
                        int safeCut = text.LastIndexOf(' ', offset + length - 1, 100);
                        if (safeCut > offset)
                        {
                            length = safeCut - offset;
                        }
                    }

                    string chunk = text.Substring(offset, length);
                    string translatedChunk = await CallTranslateApi(chunk, targetLang, ct);
                    sb.Append(translatedChunk);

                    
                    char lastChar = translatedChunk.LastOrDefault();
                    if (lastChar != '\0' && !char.IsWhiteSpace(lastChar) && lastChar != '.' && lastChar != '>')
                    {
                        sb.Append(" ");
                    }

                    offset += length;

                    if (offset < text.Length)
                    {
                        await Task.Delay(2000, ct);
                    }
                }
            }
            finally
            {
                
                playniteAPI.Notifications.Remove(LongTextNotifId);
            }

            return sb.ToString();
        }
        private async Task TranslateDescription(Game game)
        {
            if (currentlyTranslating.Contains(game.Id) || IsInCooldown)
            {
                Interlocked.Decrement(ref gamesInQueue);
                return;
            }

            currentlyTranslating.Add(game.Id);

            var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            bool isPt = playniteAPI.ApplicationSettings.Language.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (!game.IsInstalled || string.IsNullOrWhiteSpace(game.Description)) return;

                var targetLang = settingsViewModel.Settings.TargetLanguage;
                if (string.IsNullOrEmpty(targetLang)) targetLang = playniteAPI.ApplicationSettings.Language;
                var normLang = targetLang.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : targetLang.Split('-', '_')[0];
                if (!supportedLanguages.Contains(normLang)) normLang = "en";

  
                string cachedText = null;
                lock (translationCacheLock)
                {
                    if (translationCache.Cache.TryGetValue(normLang, out var entries))
                    {
                        var entry = entries.FirstOrDefault(e => string.Equals(e.Game, game.Name, StringComparison.OrdinalIgnoreCase));
                        if (entry != null && game.Description.Equals(entry.OriginalText, StringComparison.Ordinal))
                            cachedText = entry.TranslatedText;
                    }
                }

                if (cachedText != null)
                {
                    var g = playniteAPI.Database.Games.Get(game.Id);
                    if (g != null) { g.Description = cachedText; playniteAPI.Database.Games.Update(g); }
                    return;
                }



                TimeSpan timeSinceLast = DateTime.Now - lastTranslationEndTime;


                if (gamesInQueue > 1)
                {
            
                    playniteAPI.Notifications.Remove(QueueNotificationId);

                    string msg = isPt
                        ? $"Processando fila... (Restam: {gamesInQueue})"
                        : $"Processing queue... ({gamesInQueue} remaining)";

                    playniteAPI.Notifications.Add(new NotificationMessage(QueueNotificationId, msg, NotificationType.Info));
                    int delay = random.Next(3000, 6001); 
                    await Task.Delay(delay, overallCts.Token);

                }
                else
                {

                    playniteAPI.Notifications.Remove(QueueNotificationId);
                    AddTemporaryNotification("ADL_Status", isPt ? $"Traduzindo: {game.Name}" : $"Translating: {game.Name}", NotificationType.Info, 5);
                }

 
                string apiResult = await Task.Run(async () =>
                {
                    var (textWithoutHtml, restoreHtml) = PrepareAndRestoreHtmlForTranslation(game.Description);
                    string currentText = textWithoutHtml;

                    var allTitles = customTitlesFromTxt.Concat(cachedGameTitles)
                        .Where(t => t.Length > 3)
                        .OrderByDescending(t => t.Length)
                        .Select(Regex.Escape);

                    if (allTitles.Any())
                    {
                        var masterRegex = new Regex($@"\b({string.Join("|", allTitles)})\b", RegexOptions.None);
                        var (preparedForApi, restoreTitles) = PrepareAndRestoreTextForTranslation(currentText, masterRegex);

                        if (normLang != "ja") { preparedForApi = ApplyReplacements(preparedForApi, normLang); }

                        string finalText = (normLang == "pt-BR") ? ApplyExceptions(preparedForApi) : preparedForApi;

                        string translated = await TranslateTextWithLimits(finalText, normLang, overallCts.Token, game.Name);

                        if (!string.IsNullOrEmpty(translated))
                        {
                            if (normLang == "ja")
                            {
                                translated = Regex.Replace(translated, @"(HT\d+|PH\d+)\s*の特長", "$1");
                                translated = Regex.Replace(translated, @"の特長\s*(HT\d+|PH\d+)", "$1");
                                translated = translated.Replace("の特長", "").Replace("円nefer", "イェネファー").Replace("文字をチェック", "手紙を確認");
                                translated = translated.Replace("再生する", "プレイする").Replace("再生します", "プレイします");
                                translated = Regex.Replace(translated, @"([ぁ-んァ-ン一-龯])\s+([ぁ-んァ-ン一-龯])", "$1$2");
                            }
                            if (normLang == "pt-BR") { translated = FixCapitalization(NormalizeSpacingAfterReplacement(translated)); }

                            return restoreHtml(restoreTitles(translated));
                        }
                    }
                    else
                    {
                        string translated = await CallTranslateApi(currentText, normLang, overallCts.Token);
                        return restoreHtml(translated);
                    }
                    return null;
                });

                if (!string.IsNullOrEmpty(apiResult))
                {
                    lock (translationCacheLock)
                    {
                        if (!translationCache.Cache.ContainsKey(normLang)) translationCache.Cache[normLang] = new List<CachedTranslationEntry>();
                        translationCache.Cache[normLang].RemoveAll(e => string.Equals(e.Game, game.Name, StringComparison.OrdinalIgnoreCase));
                        translationCache.Cache[normLang].Add(new CachedTranslationEntry { Game = game.Name, OriginalText = game.Description, TranslatedText = apiResult });
                        SaveTranslationCache();
                    }
                    var g = playniteAPI.Database.Games.Get(game.Id);
                    if (g != null) { g.Description = apiResult; playniteAPI.Database.Games.Update(g); }
                }
            }
            catch (Exception ex) { logger.Error(ex, $"Erro na tradução de {game.Name}"); }
            finally
            {
                lastTranslationEndTime = DateTime.Now;
                currentlyTranslating.Remove(game.Id);


                Interlocked.Decrement(ref gamesInQueue);


                playniteAPI.Notifications.Remove(QueueNotificationId);

                if (gamesInQueue <= 0)
                {
                    gamesInQueue = 0;
                    largeQueueNotified = false;
                }
            }
        }
        private async Task<string> CallTranslateApi(string text, string targetLang, CancellationToken ct)
        {
            string apiUrl = $"{BaseUrl}/translate";
            var requestBody = new { q = text, source = "auto", target = targetLang, format = "text" };
            var content = new StringContent(Serialization.ToJson(requestBody), Encoding.UTF8, "application/json");

            try
            {

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                {
                    HttpResponseMessage response = await httpClient.PostAsync(apiUrl, content, linkedCts.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        int code = (int)response.StatusCode;

                        if (code == 429 || code == 503 || code == 502 || code == 504)
                        {
                            throw new TooManyRequestsException($"Servidor instável ({code})");
                        }
                        return text;
                    }

                    string responseBody = await response.Content.ReadAsStringAsync();
                    dynamic result = Serialization.FromJson<dynamic>(responseBody);
                    return result.translatedText ?? text;
                }
            }
            catch (TooManyRequestsException) { throw; }
            catch (OperationCanceledException)
            {

                throw new TooManyRequestsException("O servidor demorou demais para responder.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Erro na comunicação com a API");
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

        private string ApplyReplacements(string text, string lang)
        {
            lock (replacementsLock)
            {
              
                if (replacements != null && replacements.TryGetValue(lang, out var langRules))
                {
                    foreach (var kv in langRules.OrderByDescending(k => k.Key.Length))
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
        private void LoadTranslationCache()
        {
            try
            {
                lock (translationCacheLock)
                {
                    if (!File.Exists(translationCacheFilePath))
                    {
                        File.WriteAllText(translationCacheFilePath, Serialization.ToJson(new TranslationCache(), true), Encoding.UTF8);
                        translationCache = new TranslationCache();
                        return;
                    }
                    var json = File.ReadAllText(translationCacheFilePath, Encoding.UTF8);
                    translationCache = Serialization.FromJson<TranslationCache>(json) ?? new TranslationCache();
                    logger.Info($"[AutoDescriptionLocalizer] Cache de tradução carregado ({translationCache.Cache.Keys.Count} idiomas).");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao carregar translation_cache.json");
                translationCache = new TranslationCache();
            }
        }

        private void SaveTranslationCache()
        {
            try
            {
                lock (translationCacheLock)
                {
                    File.WriteAllText(translationCacheFilePath, Serialization.ToJson(translationCache, true), Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao salvar translation_cache.json");
            }
        }

        private void SetupTranslationCacheFileWatcher()
        {
            try
            {
                translationCacheWatcher = new FileSystemWatcher(Path.GetDirectoryName(translationCacheFilePath), Path.GetFileName(translationCacheFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                translationCacheWatcher.Changed += (s, e) => DebouncedReloadTranslationCache();
                translationCacheWatcher.Renamed += (s, e) => DebouncedReloadTranslationCache();
                translationCacheWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao configurar watcher para translation_cache.json."); }
        }

        private void DebouncedReloadTranslationCache()
        {
            translationCacheReloadTimer?.Stop();
            if (translationCacheReloadTimer == null)
            {
                translationCacheReloadTimer = new System.Timers.Timer(500) { AutoReset = false };
                translationCacheReloadTimer.Elapsed += (s, e) => LoadTranslationCache();
            }
            translationCacheReloadTimer.Start();
        }
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
                       
                        var initial = new Dictionary<string, Dictionary<string, string>>
                {
                    { "pt-BR", new Dictionary<string, string>() },
                    { "ja", new Dictionary<string, string>() }
                };
                        File.WriteAllText(replacementsFilePath, Serialization.ToJson(initial, true), Encoding.UTF8);
                        replacements = initial;
                        return;
                    }

                    var json = File.ReadAllText(replacementsFilePath, Encoding.UTF8);

                   
                    replacements = Serialization.FromJson<Dictionary<string, Dictionary<string, string>>>(json);

                    if (replacements == null)
                    {
                        replacements = new Dictionary<string, Dictionary<string, string>>(StringComparer.InvariantCultureIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[AutoDescriptionLocalizer] Falha ao carregar replacements.json.");
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