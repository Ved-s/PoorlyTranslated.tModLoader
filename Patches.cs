using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria.Localization;
using Terraria;
using Terraria.ModLoader;
using PoorlyTranslated.TranslationTasks;
using System.Reflection;
using MonoMod.RuntimeDetour.HookGen;
using MonoMod.RuntimeDetour;
using System.Threading;

namespace PoorlyTranslated
{
    public class Patches : ILoadable
    {
        delegate bool orig_IncludeURIInRequestLogging(Uri uri);
        delegate bool hook_IncludeURIInRequestLogging(orig_IncludeURIInRequestLogging orig, Uri uri);
        static MethodInfo method_IncludeURIInRequestLogging = typeof(ModLoader).Assembly.GetType("Terraria.ModLoader.Engine.LoggingHooks")!.GetMethod("IncludeURIInRequestLogging", BindingFlags.NonPublic | BindingFlags.Static)!;
        static Hook? detour_IncludeURIInRequestLogging;

        public void Load(Mod mod)
        {
            On_LanguageManager.LoadFilesForCulture += On_LanguageManager_LoadFilesForCulture;
            On_Main.Update += On_Main_Update;
            On_Main.DrawFPS += On_Main_DrawFPS;
            On_Program.RunGame += On_Program_RunGame;

            detour_IncludeURIInRequestLogging = new Hook(method_IncludeURIInRequestLogging, (hook_IncludeURIInRequestLogging)On_LoggingHooks_IncludeURIInRequestLogging);
            detour_IncludeURIInRequestLogging.Apply();
        }

        public void Unload() 
        {
            On_LanguageManager.LoadFilesForCulture -= On_LanguageManager_LoadFilesForCulture;
            On_Main.Update -= On_Main_Update;
            On_Main.DrawFPS -= On_Main_DrawFPS;
            On_Program.RunGame -= On_Program_RunGame;

            detour_IncludeURIInRequestLogging?.Dispose();
            detour_IncludeURIInRequestLogging = null;
        }
        
        private void On_Main_Update(On_Main.orig_Update orig, Main self, Microsoft.Xna.Framework.GameTime gameTime)
        {
            orig(self, gameTime);
            LocalizationsStorage.FlushLocalizations();
            ModContent.GetInstance<PoorlyTranslated>()?.Update();
        }

        private void On_LanguageManager_LoadFilesForCulture(On_LanguageManager.orig_LoadFilesForCulture orig, LanguageManager self, GameCulture culture)
        {
            if (culture == self.ActiveCulture)
            {
                ActiveTranslations.Stop(true);
            }
            orig(self, culture);
            if (culture == self.ActiveCulture)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    ModContent.GetInstance<PoorlyTranslated>()?.ResetLanguage();
                    ActiveTranslations.Load();
                    ActiveTranslations.Run();
                });
            }
        }

        private void On_Main_DrawFPS(On_Main.orig_DrawFPS orig, Main self)
        {
            ModContent.GetInstance<PoorlyTranslated>()?.DrawStatusText();

            orig(self);
        }

        private void On_Program_RunGame(On_Program.orig_RunGame orig)
        {
            orig();
            if (ActiveTranslations.Running) 
            {
                ActiveTranslations.Stop(true);
            }
        }


        private bool On_LoggingHooks_IncludeURIInRequestLogging(orig_IncludeURIInRequestLogging orig, Uri uri) 
        {
            if (uri.Host == "translate.googleapis.com")
                return false;
            return orig(uri);
        }
    }
}
