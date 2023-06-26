using log4net;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PoorlyTranslated.TranslationTasks;
using ReLogic.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.GameContent;
using Terraria.Localization;
using Terraria.ModLoader;

namespace PoorlyTranslated
{
    public class PoorlyTranslated : Mod
    {
        Stopwatch LastUpdateWatch = Stopwatch.StartNew();
        Stopwatch LastSaveWatch = Stopwatch.StartNew();
        int LastKeySaveCount = 0;

        const int MaxSampleCount = 30;
        List<int> TranslationSpeedSamples = new(MaxSampleCount);
        int? LastSampledKeyCount = null;
        PoorStringProvider? StatusProvider;

        public override void Load()
        {
            base.Load();
        }

        public override void Close()
        {
            ActiveTranslations.Stop(true);
        }

        public void Update()
        {
            ThreadedStringsTranslator.Poke();
            if (ActiveTranslations.Running)
            {
                if (StatusProvider is null && ActiveTranslations.TranslatorLang is string lang)
                    StatusProvider = new("Remaining translations: {0}\nSpeed: {1} lines/sec, ETA: {2}\nThreads active: {3}", lang, TimeSpan.FromSeconds(5), ActiveTranslations.CancellationSource.Token);
                StatusProvider?.Update();

                if (LastUpdateWatch.ElapsedMilliseconds > 1000)
                {
                    LastUpdateWatch.Restart();

                    int count;
                    lock (ActiveTranslations.Lock)
                    {
                        count = ActiveTranslations.TranslatedKeys.Count;
                    }

                    if (LastSampledKeyCount.HasValue)
                    {
                        int speed = count - LastSampledKeyCount.Value;
                        TranslationSpeedSamples.Add(speed);
                        if (TranslationSpeedSamples.Count > MaxSampleCount)
                            TranslationSpeedSamples.RemoveAt(0);
                    }
                    LastSampledKeyCount = count;

                    int diff = count - LastKeySaveCount;
                    if (diff > 200 || diff > 0 && LastSaveWatch.ElapsedMilliseconds > 10000)
                    {
                        LastKeySaveCount = count;
                        LastSaveWatch.Restart();
                        ActiveTranslations.Save();
                    }
                }

            }
            else if (TranslationSpeedSamples.Count > 0 || LastSampledKeyCount is not null)
            {
                ResetLanguage();
            }
        }

        internal void ResetLanguage()
        {
            TranslationSpeedSamples.Clear();
            LastSampledKeyCount = null;
            StatusProvider = null;
        }

        public void DrawStatusText()
        {
            if (!ActiveTranslations.Running || !Main.gameMenu && !Main.playerInventory || StatusProvider is null)
                return;

            float? tps = null;
            if (TranslationSpeedSamples.Count > 0)
            {
                tps = 0;
                foreach (int speed in TranslationSpeedSamples)
                    tps += speed;
                tps /= TranslationSpeedSamples.Count;
            }

            int remaining = ActiveTranslations.Remaining;
            float? estimatedSec = tps is null ? null : remaining / tps;

            string format = StatusProvider.Template;

            string status = string.Format(format,
                ActiveTranslations.Remaining.ToString(),
                tps?.ToString("0.##") ?? "unknown",
                (estimatedSec is null ? "unknown" : FormatTimeSec((uint)estimatedSec)),
                ThreadedStringsTranslator.ThreadsAlive);

            float scale = 0.8f;

            int lines = 3;
            float height = FontAssets.MouseText.Value.LineSpacing * scale * lines;

            Vector2 position;

            if (Main.gameMenu)
            {
                position = new(20, (Main.screenHeight - (int)height) / 2);
            }
            else
            {
                position = new(120, (int)((Main.screenHeight - (int)height) * 0.8f));
            }

            Main.spriteBatch.DrawString(FontAssets.MouseText.Value, status, position, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        static string FormatTimeSec(uint s)
        {
            uint h = s / 3600;
            s %= 3600;

            uint m = s / 60;
            s %= 60;

            if (h > 0)
                return $"{h}h {m}m {s}s";
            if (m > 0)
                return $"{m}m {s}s";
            return $"{s}s";
        }
    }

    static class ActiveTranslations
    {
        public static bool Running;

        public static GameCulture? CurrentCulture = null;

        public static Dictionary<string, (TranslationTaskBatch<string>, List<string>)> Batches = new();
        public static HashSet<string> TranslatedKeys = new();
        public static object Lock = new();
        public static CancellationTokenSource CancellationSource = new();

        static ILog Logger = LogManager.GetLogger("PoorlyTranslated.ActiveTranslations");

        public static string? TranslatorLang
        {
            get
            {
                if (CurrentCulture is null)
                    return null;

                string currentLang = CurrentCulture.Name;
                return Translator.Languages.FirstOrDefault(l =>
                    l.Equals(currentLang, StringComparison.InvariantCultureIgnoreCase)
                    || currentLang.StartsWith(l, StringComparison.InvariantCultureIgnoreCase)) ?? "en";
            }
        }

        public static int Remaining
        {
            get
            {
                lock (Lock)
                {
                    return Batches.Values.Sum(b => b.Item1.Remaining);
                }
            }
        }

        public static void Run()
        {
            if (Main.dedServ)
            {
                return;
            }

            if (Running)
            {
                Stop(true);
            }
            Running = true;

            if (LanguageManager.Instance.ActiveCulture != CurrentCulture)
            {
                TranslatedKeys.Clear();
            }
            CancellationSource = new();

            CurrentCulture = LanguageManager.Instance.ActiveCulture;
            string targetLang = TranslatorLang!;

            var texts = Wrappers.LanguageManager.LocalizedTexts;
            var groups = new Dictionary<string, List<string>>();
            lock (Lock)
            {
                foreach (string key in texts.Keys.Where(k => k.Length > 0 && !TranslatedKeys.Contains(k)))
                {
                    int dot = key.IndexOf('.');
                    string group = dot < 0 ? "Unknown" : key.Substring(0, dot);
                    if (!groups.TryGetValue(group, out var list))
                    {
                        list = new();
                        groups[group] = list;
                    }
                    list.Add(key);
                }

                foreach (var (groupname, list) in groups)
                {
                    var batch = (new TranslationTaskBatch<string>(new LocalizationsStorage(CurrentCulture), list, targetLang, 5), list);
                    Batches[groupname] = batch;

                    Task.Run(async () =>
                    {
                        try
                        {
                            await batch.Item1.Translate(CancellationSource.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        lock (Lock)
                        {
                            Batches.Remove(groupname);
                            if (Batches.Count == 0)
                            {
                                Running = false;
                                ModContent.GetInstance<PoorlyTranslated>()?.Logger.Info("Translation finish");
                                Save();
                            }
                        }
                    });
                }
            }
        }

        public static void Stop(bool save)
        {
            CancellationSource.Cancel();
            ThreadedStringsTranslator.Cancel();
            lock (Lock)
            {
                Batches.Clear();
                Running = false;
            }
            if (Running && save)
            {
                Save();
            }
        }

        public static void Load()
        {
            string dir = Path.Combine(Main.SavePath, "Translations");
            if (!Directory.Exists(dir))
                return;

            lock (Lock)
            {
                TranslatedKeys.Clear();
            }

            // {culture}(\..*).json
            string cultureDot = $"{LanguageManager.Instance.ActiveCulture.Name}.";
            foreach (string file in Directory.EnumerateFiles(dir, $"{LanguageManager.Instance.ActiveCulture.Name}*.json"))
            {
                if (!Path.GetFileName(file).StartsWith(cultureDot))
                    continue;

                LoadKeys(file);
            }

            CurrentCulture = LanguageManager.Instance.ActiveCulture;
        }

        public static void Save()
        {
            if (TranslatedKeys.Count == 0)
                return;

            string dir = Path.Combine(Main.SavePath, "Translations");
            Directory.CreateDirectory(dir);

            string jsonpath = Path.Combine(dir, $"{(CurrentCulture ?? LanguageManager.Instance.ActiveCulture).Name}.json");
            SaveKeys(jsonpath);
        }

        static void SaveKeys(string path)
        {
            JObject root = new();
            lock (Lock)
            {
                var texts = Wrappers.LanguageManager.LocalizedTexts;
                foreach (var key in TranslatedKeys)
                {
                    string text;
                    lock (LocalizationsStorage.Lock)
                    {
                        if (!texts.TryGetValue(key, out var loctext))
                            continue;

                        text = loctext.Value;
                    }

                    string[] langpath = key.Split('.');
                    JObject obj = root;
                    for (int i = 0; i < langpath.Length; i++)
                    {
                        if (i == langpath.Length - 1)
                        {
                            obj[langpath[i]] = text;
                            break;
                        }

                        if (obj.TryGetValue(langpath[i], StringComparison.InvariantCultureIgnoreCase, out JToken? next) && next is JObject nextObject)
                        {
                            obj = nextObject;
                        }
                        else
                        {
                            JObject o = new();
                            obj[langpath[i]] = o;
                            obj = o;
                        }
                    }
                }
            }

            using StreamWriter fs = File.CreateText(path);
            JsonSerializer.CreateDefault().Serialize(fs, root);
        }

        static void LoadKeys(string path)
        {
            JToken? json;
            try
            {
                using (StreamReader reader = File.OpenText(path))
                {
                    json = (JToken?)JsonSerializer.CreateDefault().Deserialize(reader, typeof(JToken))!;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error while reading {Path.GetFileNameWithoutExtension(path)} translations from storage", ex);
                return;
            }

            if (json is not JObject jsonObject)
            {
                Logger.Error($"Translations object in {Path.GetFileNameWithoutExtension(path)} was in incorrect format (expected json object)");
                return;
            }

            void LoadKeysRecursive(JObject obj, StringBuilder pathBuilder, string filename)
            {
                foreach (var (key, value) in obj)
                {
                    int builderPos = pathBuilder.Length;
                    if (pathBuilder.Length > 0)
                    {
                        pathBuilder.Append('.');
                    }
                    pathBuilder.Append(key);

                    switch (value)
                    {
                        case JObject subObject:
                            LoadKeysRecursive(subObject, pathBuilder, filename);
                            break;

                        case JValue subValue when subValue.Value is string str:
                            string tkey = pathBuilder.ToString();
                            if (tkey.Length == 0)
                            {
                                Logger.Warn($"Got empty key from {filename} {subValue.Path}");
                                break;
                            }
                            lock (LocalizationsStorage.Lock)
                            {
                                if (Wrappers.LanguageManager.LocalizedTexts.TryGetValue(tkey, out LocalizedText? text))
                                {
                                    Wrappers.LocalizedText.SetValue(text, str);
                                }
                                else
                                {
                                    Logger.Warn($"Got nonexistent key {tkey} from {filename} {subValue.Path}");
                                    break;
                                }
                            }
                            lock (Lock)
                            {
                                TranslatedKeys.Add(tkey);
                            }
                            break;
                    }

                    pathBuilder.Remove(builderPos, pathBuilder.Length - builderPos);
                }
            }
            LoadKeysRecursive(jsonObject, new StringBuilder(), Path.GetFileNameWithoutExtension(path));
        }

        static void OptimizeKeys(JObject obj)
        {
            List<string> path = new();
            List<(string, string, JToken?)> redefs = new();
            foreach (var (key, value) in obj)
            {
                if (value is not JObject subObj)
                    continue;

                path.Clear();
                path.Add(key);

                JToken? v = subObj;
                while (v is JObject o && o.Count == 1)
                {
                    bool stop = false;
                    foreach (var (subkey, subvalue) in o)
                    {
                        if (subkey is null)
                        {
                            stop = true;
                            break;
                        }

                        path.Add(subkey);
                        v = subvalue;
                        break;
                    }
                    if (stop) break;
                }

                if (v is JObject nestedObj)
                    OptimizeKeys(nestedObj);

                if (path.Count <= 1)
                    continue;

                redefs.Add((key, string.Join('.', path), v));
            }
            foreach (var redef in redefs)
            {
                obj.Remove(redef.Item1);
                obj[redef.Item2] = redef.Item3;
            }
        }
    }
}