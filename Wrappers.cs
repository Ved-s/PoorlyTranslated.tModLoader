using System.Collections.Generic;
using Terraria.Localization;

namespace PoorlyTranslated
{
    public static class Wrappers 
    {
        public static ILanguageManagerWrapper LanguageManager = TypeWrapping.CreateWrapper<ILanguageManagerWrapper>(Terraria.Localization.LanguageManager.Instance);
        public static ILocalizedTextWrapper LocalizedText = TypeWrapping.CreateWrapper<ILocalizedTextWrapper>(typeof(LocalizedText), null!);

        public interface ILanguageManagerWrapper
        {
            [TargetMember("_localizedTexts")]
            Dictionary<string, LocalizedText> LocalizedTexts { get; }
        }

        public interface ILocalizedTextWrapper
        {
            void SetValue([InstanceParameter] LocalizedText text, string value);
        }
    }
}