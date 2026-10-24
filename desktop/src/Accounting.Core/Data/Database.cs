using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Accounting.Core.Data;

/// <summary>
/// One company = one SQLite file. Reads run in parallel (WAL mode); writes are
/// serialised by a process-wide lock plus BEGIN IMMEDIATE, which plays the role
/// of PostgreSQL's SELECT ... FOR UPDATE row locks in the old backend: two sales
/// (one from the desktop, one from the phone) can never both take the same stock.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile int _writingThread;

    /// <summary>Today's business date. Tests replace it to get fixed dates.</summary>
    public Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    public string Path { get; }

    private Database(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    static Database()
    {
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
    }

    /// <summary>Open (and create or upgrade) the company file at <paramref name="path"/>.</summary>
    public static Database Open(string path)
    {
        var db = new Database(path);
        db.Migrate();
        return db;
    }

    public SqliteConnection Connect()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Migrate()
    {
        using var conn = Connect();
        conn.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;");
        var version = conn.ExecuteScalar<long>("PRAGMA user_version");
        for (var i = (int)version; i < Schema.Migrations.Length; i++)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(Schema.Migrations[i], transaction: tx);
            conn.Execute($"PRAGMA user_version = {i + 1}", transaction: tx);
            tx.Commit();
        }
    }

    public T Read<T>(Func<Tx, T> work)
    {
        using var conn = Connect();
        return work(new Tx(conn, null, this));
    }

    /// <summary>Run <paramref name="work"/> in one write transaction; everything or nothing is saved.</summary>
    public T Write<T>(Func<Tx, T> work)
    {
        // A second Write on the same thread would wait for itself forever.
        if (_writingThread == Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Database.Write() cannot be nested; pass the open Tx down instead.");
        _writeLock.Wait();
        _writingThread = Environment.CurrentManagedThreadId;
        try
        {
            using var conn = Connect();
            using var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            var result = work(new Tx(conn, tx, this));
            tx.Commit();
            return result;
        }
        finally
        {
            _writingThread = 0;
            _writeLock.Release();
        }
    }

    public void Write(Action<Tx> work) => Write<object?>(t => { work(t); return null; });

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value) => parameter.Value = value.ToString("yyyy-MM-dd");
        public override DateOnly Parse(object value) => DateOnly.Parse((string)value);
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly? value) =>
            parameter.Value = value is { } d ? d.ToString("yyyy-MM-dd") : DBNull.Value;
        public override DateOnly? Parse(object value) => value is string s ? DateOnly.Parse(s) : null;
    }
}

/// <summary>An open connection (and, for writes, its transaction) handed to services.</summary>
public sealed class Tx(SqliteConnection connection, SqliteTransaction? transaction, Database database)
{
    public SqliteConnection Connection { get; } = connection;
    public SqliteTransaction? Transaction { get; } = transaction;
    public Database Database { get; } = database;
    public bool IsWrite => Transaction is not null;

    public DateOnly Today => Database.Today();

    public void RequireWrite()
    {
        if (!IsWrite)
            throw new InvalidOperationException("This operation must run inside Database.Write().");
    }

    public int Execute(string sql, object? param = null) => Connection.Execute(sql, param, Transaction);

    public T? Scalar<T>(string sql, object? param = null) => Connection.ExecuteScalar<T>(sql, param, Transaction);

    public long Insert(string sql, object? param = null) =>
        Connection.ExecuteScalar<long>(sql + "; SELECT last_insert_rowid();", param, Transaction);

    public IEnumerable<T> Query<T>(string sql, object? param = null) => Connection.Query<T>(sql, param, Transaction);

    public T? QueryFirst<T>(string sql, object? param = null) => Connection.QueryFirstOrDefault<T>(sql, param, Transaction);
}
