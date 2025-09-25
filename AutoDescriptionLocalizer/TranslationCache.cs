// TranslationCache.cs
using System.Collections.Generic;

namespace AutoDescriptionLocalizer
{
    public class CachedTranslationEntry
    {
        public string Game { get; set; }
        public string OriginalText { get; set; }
        public string TranslatedText { get; set; }
    }

    public class TranslationCache
    {
        public Dictionary<string, List<CachedTranslationEntry>> Cache { get; set; } = new Dictionary<string, List<CachedTranslationEntry>>();
    }
}