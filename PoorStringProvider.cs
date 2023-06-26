using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace PoorlyTranslated
{
    public class PoorStringProvider
    {
        private readonly string template;
        private readonly string Lang;
        private readonly TimeSpan Interval;
        private readonly CancellationToken Cancellation;
        private string? TranslatedTemplate;
        Translator Translator;
        Stopwatch UpdateWatch = Stopwatch.StartNew();
        bool InProgress = false;

        public string Template => TranslatedTemplate ?? template;

        public PoorStringProvider(string template, string lang, TimeSpan interval, CancellationToken cancellation)
        {
            this.template = template;
            Lang = lang;
            Interval = interval;
            Cancellation = cancellation;
            Translator = new();
        }

        public void Update() 
        {
            if (InProgress)
                return;

            if (UpdateWatch.Elapsed >= Interval)
            {
                UpdateWatch.Restart();

                if (Cancellation.IsCancellationRequested)
                {
                    TranslatedTemplate = null;
                    return;
                }

                InProgress = true;
                ThreadPool.QueueUserWorkItem(async (_) => 
                {
                    TranslatedTemplate = await Translator.PoorlyTranslate(Lang, template, 5, Cancellation);
                    InProgress = false;
                });
            }
        }
    }
}
