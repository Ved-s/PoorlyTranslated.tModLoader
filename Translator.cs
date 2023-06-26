using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PoorlyTranslated
{
    public class Translator
    {
        HttpClient Client = new();
        Random Random = new();

        public readonly static string[] Languages = new[] { 
            "af", "ak", "am", "ar", "as", "ay", "az", "be", "bg", "bho", "bm", "bn", "bs", "ca", "ceb",
            "ckb", "co", "cs", "cy", "da", "de", "doi", "dv", "ee", "el", "en", "eo", "es", "et", "eu",
            "fa", "fi", "fr", "fy", "ga", "gd", "gl", "gn", "gom", "gu", "ha", "haw", "hi", "hmn", "hr",
            "ht", "hu", "hy", "id", "ig", "ilo", "is", "it", "iw", "ja", "jw", "ka", "kk", "km", "kn",
            "ko", "kri", "ku", "ky", "la", "lb", "lg", "ln", "lo", "lt", "lus", "lv", "mai", "mg", "mi",
            "mk", "ml", "mn", "mni-Mtei", "mr", "ms", "mt", "my", "ne", "nl", "no", "nso", "ny", "om",
            "or", "pa", "pl", "ps", "pt", "qu", "ro", "ru", "rw", "sa", "sd", "si", "sk", "sl", "sm", "sn",
            "so", "sq", "sr", "st", "su", "sv", "sw", "ta", "te", "tg", "th", "ti", "tk", "tl", "tr", "ts",
            "tt", "ug", "uk", "ur", "uz", "vi", "xh", "yi", "yo", "zh-CN", "zh-TW", "zu" 
        };

        public static long TranslationsDone;

        public const string AutoLang = "auto";

        static ILog Logger = LogManager.GetLogger("PoorlyTranslated.Translator");

        public Translator() 
        {
            ServicePointManager.DefaultConnectionLimit = 1000;
        }

        public async Task<string?> PoorlyTranslate(string lang, string text, int times, CancellationToken? cancel = null)
        {
            string orig = text;
            bool success;
            for (int i = 0; i < times; i++)
            {
                cancel?.ThrowIfCancellationRequested();

                (success, var newText) = await Translate(AutoLang, Languages[Random.Next(Languages.Length)], text, cancel);
                if (!success)
                    return null;

                text = newText;
            }

            cancel?.ThrowIfCancellationRequested();
            (success, var final) = await Translate(AutoLang, lang, text, cancel);
            if (!success)
                return null;

            return FixString(orig, final);
        }

        public async Task<(bool, string)> Translate(string srcLang, string dstLang, string text, CancellationToken? cancel = null)
        {
            string url = $"http://translate.googleapis.com/translate_a/single?client=gtx&sl={srcLang}&tl={dstLang}&dt=t&q={text}";
            HttpResponseMessage response = await Client.GetAsync(url, cancel ?? CancellationToken.None);

            if (!response.IsSuccessStatusCode)
                return (false, $"Response: {(int)response.StatusCode} {response.StatusCode}");

            cancel?.ThrowIfCancellationRequested();

            string json = await response.Content.ReadAsStringAsync();
            JToken jtoken = JToken.Parse(json);

            if (jtoken is JArray array0 && array0.FirstOrDefault() is JArray array1)
            {
                StringBuilder builder = new();

                foreach (JToken obj in array1)
                {
                    if (obj is JArray array2 && array2.FirstOrDefault() is JValue value && value.Value is string valuestr)
                        builder.Append(valuestr);
                }

                Interlocked.Increment(ref TranslationsDone);
                return (true, builder.ToString());
            }

            return (false, $"JsonError({srcLang}, {dstLang}, {text}): {json}");
        }

        public string FixString(string old, string @new)
        {

            const char FixStart = '{';
            const char FixEnd = '}';

            if (old.Contains(FixStart) && old.Contains(FixEnd))
            {
                StringBuilder builder = new();
                int pos = 0;
                Queue<string> keys = new();

                while (true)
                {
                    int startIndex = old.IndexOf(FixStart, pos);
                    if (startIndex < 0)
                        break;
                    startIndex++;

                    int endIndex = old.IndexOf(FixEnd, startIndex);
                    if (endIndex < 0)
                        break;

                    keys.Enqueue(old.Substring(startIndex, endIndex - startIndex));
                    pos = endIndex + 1;
                }

                pos = 0;
                while (keys.Count > 0 && pos < @new.Length)
                {
                    string key = keys.Dequeue();

                    int startIndex = @new.IndexOf(FixStart, pos);
                    if (startIndex < 0)
                        break;

                    int endIndex = @new.IndexOf(FixEnd, startIndex);
                    if (endIndex < 0)
                        break;

                    builder.Append(@new, pos, startIndex - pos);
                    builder.Append(FixStart);
                    builder.Append(key);
                    builder.Append(FixEnd);
                    pos = endIndex + 1;
                }

                if (pos < @new.Length)
                    builder.Append(@new, pos, @new.Length - pos);

                @new = builder.ToString();
            }

            if (old.StartsWith(' ') && !@new.StartsWith(' '))
                @new = ' ' + @new;

            if (old.EndsWith(' ') && !@new.EndsWith(' '))
                @new += ' ';

            return Regex.Replace(@new, @"{\s|\s}|{(?!.+})|(?<!{.+)}", "");
        }
    }
}
