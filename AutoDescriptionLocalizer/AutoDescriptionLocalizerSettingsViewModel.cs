// AutoDescriptionLocalizerSettingsViewModel.cs
using Playnite.SDK;
using Playnite.SDK.Plugins;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace AutoDescriptionLocalizer
{
    public class LanguageItem : ObservableObject
    {
        private string displayName;
        public string DisplayName { get => displayName; set => SetValue(ref displayName, value); }
        public string Value { get; set; }
    }

    public class AutoDescriptionLocalizerSettingsViewModel : ObservableObject, ISettings
    {
        private readonly AutoDescriptionLocalizerPlugin plugin;
        private AutoDescriptionLocalizerSettings settings;
        private Dictionary<string, string> translations = new Dictionary<string, string>();
        private readonly string initialPlayniteLanguage;

        public AutoDescriptionLocalizerSettings Settings { get => settings; set { settings = value; OnPropertyChanged(); } }
        public ObservableCollection<LanguageItem> AvailableLanguages { get; } = new ObservableCollection<LanguageItem>();

        public string WindowTitle => GetString("WindowTitle");
        public string TargetLanguageLabel => GetString("TargetLanguageLabel");
        public string TargetLanguageHelp => GetString("TargetLanguageHelp");
        public string AskBeforeOverwriteLabel => GetString("AskBeforeOverwriteLabel");
        // Adicione esta propriedade abaixo de AskBeforeOverwriteLabel
        public string ClearCacheButtonLabel => GetString("ClearCacheButtonLabel");
        public string ClearCacheConfirmationMessage => GetString("ClearCacheConfirmationMessage");
        public string ClearCacheSuccessMessage => GetString("ClearCacheSuccessMessage");

        private RelayCommand clearCacheCommand;
        public RelayCommand ClearCacheCommand
        {
            get
            {
                if (clearCacheCommand == null)
                {
                    clearCacheCommand = new RelayCommand(() =>
                    {
                        var playniteAPI = plugin.PlayniteApi;

                        // 1. Confirmação
                        var confirmResult = playniteAPI.Dialogs.ShowMessage(
                            ClearCacheConfirmationMessage,
                            WindowTitle,
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Warning);

                        if (confirmResult == System.Windows.MessageBoxResult.Yes)
                        {
                            // 2. Chamada ao método do plugin para limpar o cache
                            plugin.ClearTranslationCache();

                            // 3. Notificação de sucesso
                            playniteAPI.Dialogs.ShowMessage(
                                ClearCacheSuccessMessage,
                                WindowTitle,
                                System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Information);
                        }
                    });
                }
                return clearCacheCommand;
            }
        }
        public AutoDescriptionLocalizerSettingsViewModel(AutoDescriptionLocalizerPlugin plugin, AutoDescriptionLocalizerSettings settings, string playniteLanguage)
        {
            this.plugin = plugin;
            this.settings = settings;
            this.initialPlayniteLanguage = playniteLanguage;

            InitializeAvailableLanguages();
            ReloadUI(false); // Carrega a UI inicial sem disparar um evento de atualização completo
        }

        private void InitializeAvailableLanguages()
        {
            AvailableLanguages.Add(new LanguageItem { Value = string.Empty });
            AvailableLanguages.Add(new LanguageItem { Value = "en" });
            AvailableLanguages.Add(new LanguageItem { Value = "pt" });
            AvailableLanguages.Add(new LanguageItem { Value = "es" });
            AvailableLanguages.Add(new LanguageItem { Value = "de" });
            AvailableLanguages.Add(new LanguageItem { Value = "fr" });
            AvailableLanguages.Add(new LanguageItem { Value = "ru" });
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
           
            if (e.PropertyName == nameof(Settings.TargetLanguage))
            {
              
                settings.PropertyChanged -= Settings_PropertyChanged;
                try
                {
                    ReloadUI();
                }
                finally
                {
                    settings.PropertyChanged += Settings_PropertyChanged;
                }
            }
        }

        private void ReloadUI(bool notifyAll = true)
        {
            var uiLang = initialPlayniteLanguage; 
            var selectedLang = settings.TargetLanguage;

            if (!string.IsNullOrEmpty(selectedLang))
            {
                if (selectedLang.StartsWith("pt", System.StringComparison.OrdinalIgnoreCase))
                {
                    uiLang = "pt_BR";
                }
                else
                {
                    uiLang = "en"; 
                }
            }

            LoadTranslations(uiLang);
            UpdateLanguageDisplayNames();

            if (notifyAll)
            {
                OnPropertyChanged(string.Empty);
            }
        }

        private void LoadTranslations(string language)
        {
            translations = new Dictionary<string, string>
            {
                { "WindowTitle", "Translation Settings" },
                { "TargetLanguageLabel", "Target Translation Language" },
                { "TargetLanguageHelp", "If 'Default' is selected, the extension will use your Playnite language." },
                { "AskBeforeOverwriteLabel", "Always ask before overwriting the description" },
                { "ClearCacheButtonLabel", "Clear Translation Cache" },
                { "ClearCacheConfirmationMessage", "Are you sure you want to delete all cached translations? This action cannot be undone." },
                { "ClearCacheSuccessMessage", "Translation cache successfully cleared!" },
                { "LangDefault", "Default (Playnite System Language)" },
                { "LangEnglish", "English (en)" },
                { "LangPortuguese", "Portuguese (pt-BR)" },
                { "LangSpanish", "Spanish (es)" },
                { "LangGerman", "German (de)" },
                { "LangRussian", "French (fr)" },
                { "LangChinese", "Russian (ru)" }
            };
            if (language?.StartsWith("pt", System.StringComparison.OrdinalIgnoreCase) == true)
            {
                translations["WindowTitle"] = "Configurações de Tradução";
                translations["TargetLanguageLabel"] = "Idioma de Destino da Tradução";
                translations["TargetLanguageHelp"] = "Se 'Padrão' for selecionado, a extensão usará o idioma do seu Playnite.";
                translations["AskBeforeOverwriteLabel"] = "Sempre perguntar antes de sobrescrever a descrição";
                translations["ClearCacheButtonLabel"] = "Limpar Cache de Tradução";
                translations["ClearCacheConfirmationMessage"] = "Tem certeza de que deseja deletar todas as traduções em cache? Esta ação não pode ser desfeita.";
                translations["ClearCacheSuccessMessage"] = "Cache de tradução limpo com sucesso!";
                translations["LangDefault"] = "Padrão (Idioma do Playnite)";
            }
        }

        private string GetString(string key) => translations.TryGetValue(key, out var value) ? value : $"[{key}]";

        private void UpdateLanguageDisplayNames()
        {
            AvailableLanguages[0].DisplayName = GetString("LangDefault"); AvailableLanguages[1].DisplayName = GetString("LangEnglish"); AvailableLanguages[2].DisplayName = GetString("LangPortuguese"); AvailableLanguages[3].DisplayName = GetString("LangSpanish"); AvailableLanguages[4].DisplayName = GetString("LangGerman"); AvailableLanguages[5].DisplayName = GetString("LangRussian"); AvailableLanguages[6].DisplayName = GetString("LangChinese");
        }

        // A lógica de Begin/End Edit gerencia o ciclo de vida do "ouvinte"
        public void BeginEdit() { this.settings.PropertyChanged += Settings_PropertyChanged; }
        public void CancelEdit() { this.settings.PropertyChanged -= Settings_PropertyChanged; }
        public void EndEdit() { this.settings.PropertyChanged -= Settings_PropertyChanged; plugin.SavePluginSettings(settings); }
        public bool VerifySettings(out List<string> errors) { errors = new List<string>(); return true; }
    }
}