using Playnite.SDK;
using System.Collections.Generic;

namespace AutoDescriptionLocalizer
{
    public class AutoDescriptionLocalizerSettings : ObservableObject
    {
        private string targetLanguage = string.Empty;
        private bool askBeforeOverwrite = true;
        public int Timeout { get; set; } = 60; // Padrão de 60 segundos

        public string TargetLanguage { get => targetLanguage; set => SetValue(ref targetLanguage, value); }
        public bool AskBeforeOverwrite { get => askBeforeOverwrite; set => SetValue(ref askBeforeOverwrite, value); }
    }
}