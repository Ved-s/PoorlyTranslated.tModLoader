using System.Collections.Generic;
using Terraria.Localization;

namespace PoorlyTranslated.TranslationTasks
{
    public class LocalizationsStorage : StringStorage<string>
    {
        public Dictionary<string, LocalizedText> Dictionary => Wrappers.LanguageManager.LocalizedTexts;
        public override IEnumerable<string> Keys => Dictionary.Keys;
        public GameCulture CurrentCulture;

        public LocalizationsStorage(GameCulture currentCulture)
        {
            CurrentCulture = currentCulture;
        }

        public static object Lock = new();
        static Dictionary<string, string> IntermediateStorage = new();

        public override bool TryGet(string key, out string value)
        {
            value = null!;

            if (LanguageManager.Instance.ActiveCulture != CurrentCulture)
                return false;

            bool result;
            lock (Lock)
            {
                if (result = Dictionary.TryGetValue(key, out var text))
                {
                    value = text!.Value;
                }
            }
            return result;
        }

        public override void Set(string key, string value)
        {
            lock (Lock)
            {
                IntermediateStorage[key] = value;
            }
            lock (ActiveTranslations.Lock) 
            {
                ActiveTranslations.TranslatedKeys.Add(key);
            }
        }

        public static void FlushLocalizations() 
        {
            lock (Lock) 
            {
                if (IntermediateStorage.Count == 0)
                    return;

                var dictionary = Wrappers.LanguageManager.LocalizedTexts;
                foreach (var (key, value) in IntermediateStorage) 
                {
                    if (dictionary.TryGetValue(key, out var text)) 
                    {
                        Wrappers.LocalizedText.SetValue(text, value);
                    }
                }
            }
        }
    }
}
