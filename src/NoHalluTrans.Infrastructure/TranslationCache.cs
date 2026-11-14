using System.Security.Cryptography;
using System.Text;
using NoHalluTrans.Core;
using Microsoft.Data.Sqlite;

namespace NoHalluTrans.Infrastructure;

public sealed class TranslationCache : ITranslationCache
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    static TranslationCache()
    {
        if (OperatingSystem.IsWindows()) SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        else
        {
            if (OperatingSystem.IsLinux()) System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
                typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
                (name, assembly, path) => name == "sqlite3" ? System.Runtime.InteropServices.NativeLibrary.Load("libsqlite3.so.0") : IntPtr.Zero);
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        }
        SQLitePCL.raw.FreezeProvider();
    }
    public TranslationCache(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS translations (
              source_language TEXT NOT NULL, target_language TEXT NOT NULL,
              source_hash BLOB NOT NULL CHECK(length(source_hash) = 32),
              source_html TEXT NOT NULL, translated_html TEXT NOT NULL DEFAULT '',
              origin TEXT NOT NULL, updated_at INTEGER NOT NULL,
              PRIMARY KEY(source_language, target_language, source_hash)
            ) WITHOUT ROWID;
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }
    public string? Get(string sourceLanguage, string targetLanguage, string text)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT translated_html FROM translations WHERE source_language=$src AND target_language=$dst AND source_hash=$hash";
            Bind(command, sourceLanguage, targetLanguage, text);
            string? result = command.ExecuteScalar() as string;
            return string.IsNullOrEmpty(result) ? null : result;
        }
    }
    public void Put(string sourceLanguage, string targetLanguage, string text, string translated, string origin = "model")
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO translations(source_language,target_language,source_hash,source_html,translated_html,origin,updated_at)
                VALUES($src,$dst,$hash,$text,$translation,$origin,$time)
                ON CONFLICT(source_language,target_language,source_hash) DO UPDATE SET
                  source_html=excluded.source_html,translated_html=excluded.translated_html,
                  origin=excluded.origin,updated_at=excluded.updated_at
                """;
            Bind(command, sourceLanguage, targetLanguage, text);
            command.Parameters.AddWithValue("$text", text); command.Parameters.AddWithValue("$translation", translated);
            command.Parameters.AddWithValue("$origin", origin); command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }
    private static void Bind(SqliteCommand command, string source, string target, string text)
    {
        command.Parameters.AddWithValue("$src", source); command.Parameters.AddWithValue("$dst", target);
        command.Parameters.AddWithValue("$hash", SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    public void Dispose() => connection.Dispose();
}
