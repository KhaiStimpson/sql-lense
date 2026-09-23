using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace SqlLense.Schema
{
    /// <summary>
    /// Process-wide cache of loaded schema snapshots. The file is stat'ed at most once per
    /// <see cref="RecheckInterval"/> and only re-read when its timestamp or size changes, so asking
    /// for the schema on every keystroke-triggered compilation is effectively free.
    /// </summary>
    public static class SchemaCache
    {
        public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(2);

        private static readonly ConcurrentDictionary<string, Entry> s_entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static DatabaseSchema? TryGet(string? path) => TryGet(path, out _);

        public static DatabaseSchema? TryGet(string? path, out string? error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var now = Stopwatch.GetTimestamp();
            if (s_entries.TryGetValue(path!, out var entry) &&
                now - entry.CheckedAt < RecheckInterval.TotalSeconds * Stopwatch.Frequency)
            {
                error = entry.Error;
                return entry.Schema;
            }

            long length;
            DateTime lastWrite;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    s_entries[path!] = new Entry(null, default, 0, now, null);
                    return null;
                }

                length = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = ex.Message;
                s_entries[path!] = new Entry(null, default, 0, now, error);
                return null;
            }

            if (entry != null && entry.LastWrite == lastWrite && entry.Length == length)
            {
                s_entries[path!] = entry.WithCheckedAt(now);
                error = entry.Error;
                return entry.Schema;
            }

            DatabaseSchema? schema = null;
            try
            {
                schema = SchemaSnapshotSerializer.Load(path!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Most likely caught mid-write by another process; the next check will retry.
                error = ex.Message;
            }

            s_entries[path!] = new Entry(schema, lastWrite, length, now, error);
            return schema;
        }

        /// <summary>Forces the next <see cref="TryGet(string?)"/> to hit the file system.</summary>
        public static void Invalidate(string path) => s_entries.TryRemove(path, out _);

        private sealed class Entry
        {
            public Entry(DatabaseSchema? schema, DateTime lastWrite, long length, long checkedAt, string? error)
            {
                Schema = schema;
                LastWrite = lastWrite;
                Length = length;
                CheckedAt = checkedAt;
                Error = error;
            }

            public DatabaseSchema? Schema { get; }

            public DateTime LastWrite { get; }

            public long Length { get; }

            public long CheckedAt { get; }

            public string? Error { get; }

            public Entry WithCheckedAt(long checkedAt) => new Entry(Schema, LastWrite, Length, checkedAt, Error);
        }
    }
}
