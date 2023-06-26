﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PoorlyTranslated.TranslationTasks
{
    public class TranslationTaskBatch<TKey>
    {
        public StringStorage<TKey> Storage { get; }
        public string Language { get; }
        public int Iterations { get; }
        public int Remaining => RemainingKeys.Count;

        internal HashSet<TKey> RemainingKeys;
        internal object Lock = new();
        TaskCompletionSource<bool> TaskCompletion = new();
        bool Completed = false;

        internal CancellationToken Cancellation = CancellationToken.None;

        public TranslationTaskBatch(StringStorage<TKey> storage, string language, int iterations) :
            this(storage, storage.Keys, language, iterations)
        { }
        public TranslationTaskBatch(StringStorage<TKey> storage, IEnumerable<TKey> keys, string language, int iterations)
        {
            Storage = storage;
            RemainingKeys = new(keys);
            Language = language;
            Iterations = iterations;
        }

        public void Cancel() 
        {
            TaskCompletion.TrySetCanceled();
        }

        public async Task Translate(CancellationToken? cancel)
        {
            if (RemainingKeys.Count == 0)
                return;

            Cancellation = cancel ?? CancellationToken.None;

            ThreadedStringsTranslator.AddTasks(RemainingKeys.Select(k => new TranslationTask<TKey>(this, k)));

            await TaskCompletion.Task;
        }

        internal void SetResult(TKey key, string str)
        {
            lock (Lock)
            {
                if (!RemainingKeys.Contains(key))
                    return;

                RemainingKeys.Remove(key);
                Storage.Set(key, str);

                if (RemainingKeys.Count == 0 && !Completed)
                {
                    Completed = true;
                    TaskCompletion.SetResult(true);
                }
            }
        }
    }
}
