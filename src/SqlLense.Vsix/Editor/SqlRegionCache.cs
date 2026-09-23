using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using SqlLense.CodeFixes;

namespace SqlLense.Vsix.Editor
{
    /// <summary>
    /// Tracks the SQL strings in one C# buffer. Recomputed on a background thread, debounced after
    /// edits, from Roslyn's incrementally parsed syntax tree (no semantic model). Consumers read the
    /// last result and translate it to newer snapshots.
    /// </summary>
    internal sealed class SqlRegionCache
    {
        private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(150);

        private readonly ITextBuffer _buffer;
        private CancellationTokenSource? _pending;
        private volatile Result? _current;

        private SqlRegionCache(ITextBuffer buffer)
        {
            _buffer = buffer;
            _buffer.Changed += (_, _) => Schedule(s_debounce);
            Schedule(TimeSpan.Zero);
        }

        /// <summary>Raised (on a background thread) when regions for a new snapshot are available.</summary>
        public event EventHandler<SnapshotSpanEventArgs>? Changed;

        public Result? Current => _current;

        public static SqlRegionCache For(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(typeof(SqlRegionCache), () => new SqlRegionCache(buffer));

        /// <summary>Regions for exactly <paramref name="snapshot"/>, computing them now if the cache is stale.</summary>
        public async Task<Result?> GetForSnapshotAsync(ITextSnapshot snapshot, CancellationToken cancellationToken)
        {
            var current = _current;
            if (current != null && current.Snapshot == snapshot)
            {
                return current;
            }

            return await ComputeAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        private void Schedule(TimeSpan delay)
        {
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _pending, cts)?.Cancel();
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                    }

                    var snapshot = _buffer.CurrentSnapshot;
                    var result = await ComputeAsync(snapshot, cts.Token).ConfigureAwait(false);
                    if (result != null && !cts.IsCancellationRequested)
                    {
                        _current = result;
                        Changed?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private static async Task<Result?> ComputeAsync(ITextSnapshot snapshot, CancellationToken cancellationToken)
        {
            var document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                return null;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return null;
            }

            return new Result(snapshot, document, SqlEditorServices.ComputeRegions(root, cancellationToken));
        }

        internal sealed class Result
        {
            public Result(ITextSnapshot snapshot, Document document, SqlDocumentRegions regions)
            {
                Snapshot = snapshot;
                Document = document;
                Regions = regions;
            }

            public ITextSnapshot Snapshot { get; }

            public Document Document { get; }

            public SqlDocumentRegions Regions { get; }
        }
    }
}
