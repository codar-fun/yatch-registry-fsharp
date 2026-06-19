module Yatch.Db

open System
open System.Data.Common
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Npgsql

/// Metadata store backed by SQLite (standalone) or Postgres (shared across
/// registry instances). Schema + queries match the Rust yatch so an existing
/// Postgres `yatch` database is read/written identically.
type Db =
    | Sqlite of connStr: string
    | Postgres of connStr: string

type ManifestRow =
    { Digest: string
      ContentType: string
      Size: int64 }

type UploadRow =
    { Repo: string
      Offset: int64
      Digest: string option
      S3UploadId: string option
      Parts: string option }

// ── Connection setup ──────────────────────────────────────────────────────────

let private pgConnStr (url: string) =
    let u = Uri(url)
    let parts = u.UserInfo.Split(':')
    let user = Uri.UnescapeDataString parts.[0]
    let pass = if parts.Length > 1 then Uri.UnescapeDataString parts.[1] else ""
    let db = u.AbsolutePath.TrimStart('/')
    let port = if u.Port > 0 then u.Port else 5432
    sprintf
        "Host=%s;Port=%d;Username=%s;Password=%s;Database=%s;Timeout=15;Command Timeout=60;Maximum Pool Size=20"
        u.Host
        port
        user
        pass
        db

let openDb (databaseUrl: string option) (dbPath: string) : Db =
    match databaseUrl with
    | Some url when url.StartsWith "postgres://" || url.StartsWith "postgresql://" -> Postgres(pgConnStr url)
    | _ ->
        let p = if dbPath.StartsWith "sqlite:" then dbPath.Substring "sqlite:".Length else dbPath
        let p = match p.IndexOf '?' with | -1 -> p | i -> p.Substring(0, i)
        Sqlite(sprintf "Data Source=%s" p)

let private newConn (db: Db) : DbConnection =
    match db with
    | Sqlite cs -> new SqliteConnection(cs) :> DbConnection
    | Postgres cs -> new NpgsqlConnection(cs) :> DbConnection

let private addParam (cmd: DbCommand) (name: string) (value: obj) =
    let p = cmd.CreateParameter()
    p.ParameterName <- name
    p.Value <- (if isNull value then box DBNull.Value else value)
    cmd.Parameters.Add p |> ignore

let private optObj (o: string option) : obj =
    match o with
    | Some s -> box s
    | None -> box DBNull.Value

let private getStrOpt (r: DbDataReader) (i: int) =
    if r.IsDBNull i then None else Some(r.GetString i)

let private execNonQuery (db: Db) (sql: string) (ps: (string * obj) list) : Task<unit> =
    task {
        use conn = newConn db
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        for (n, v) in ps do
            addParam cmd n v
        let! _ = cmd.ExecuteNonQueryAsync()
        return ()
    }

// ── Migrations ────────────────────────────────────────────────────────────────
// BIGINT and quoted "offset" work in both SQLite and Postgres; ON CONFLICT is
// supported by both (SQLite >= 3.24, bundled by Microsoft.Data.Sqlite).

let migrate (db: Db) : Task<unit> =
    task {
        // WAL is persistent (database-level) and improves read/write concurrency
        // for the standalone SQLite path.
        match db with
        | Sqlite _ -> do! execNonQuery db "PRAGMA journal_mode=WAL" []
        | Postgres _ -> ()

        do!
            execNonQuery
                db
                "CREATE TABLE IF NOT EXISTS tags (
                    repo    TEXT   NOT NULL,
                    tag     TEXT   NOT NULL,
                    digest  TEXT   NOT NULL,
                    created BIGINT NOT NULL DEFAULT 0,
                    PRIMARY KEY (repo, tag))"
                []

        do!
            execNonQuery
                db
                "CREATE TABLE IF NOT EXISTS manifests (
                    repo         TEXT   NOT NULL,
                    digest       TEXT   NOT NULL,
                    content_type TEXT   NOT NULL,
                    size         BIGINT NOT NULL,
                    created      BIGINT NOT NULL DEFAULT 0,
                    PRIMARY KEY (repo, digest))"
                []

        do!
            execNonQuery
                db
                "CREATE TABLE IF NOT EXISTS uploads (
                    uuid         TEXT PRIMARY KEY,
                    repo         TEXT   NOT NULL,
                    \"offset\"   BIGINT NOT NULL DEFAULT 0,
                    digest       TEXT,
                    s3_upload_id TEXT,
                    parts        TEXT,
                    created      BIGINT NOT NULL DEFAULT 0)"
                []
    }

let private nowSecs () = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

// ── Manifests ─────────────────────────────────────────────────────────────────

let putManifest (db: Db) (repo: string) (digest: string) (contentType: string) (size: int64) : Task<unit> =
    execNonQuery
        db
        "INSERT INTO manifests (repo, digest, content_type, size, created)
         VALUES (@repo, @digest, @ct, @size, @created)
         ON CONFLICT (repo, digest) DO UPDATE SET content_type = excluded.content_type, size = excluded.size"
        [ "@repo", box repo
          "@digest", box digest
          "@ct", box contentType
          "@size", box size
          "@created", box (nowSecs ()) ]

let getManifest (db: Db) (repo: string) (reference: string) : Task<ManifestRow option> =
    task {
        let! digest =
            if reference.StartsWith "sha256:" then
                Task.FromResult(Some reference)
            else
                task {
                    use conn = newConn db
                    do! conn.OpenAsync()
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <- "SELECT digest FROM tags WHERE repo = @repo AND tag = @tag"
                    addParam cmd "@repo" repo
                    addParam cmd "@tag" reference
                    use! r = cmd.ExecuteReaderAsync()
                    let! has = r.ReadAsync()
                    return (if has then Some(r.GetString 0) else None)
                }

        match digest with
        | None -> return None
        | Some dg ->
            use conn = newConn db
            do! conn.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT digest, content_type, size FROM manifests WHERE repo = @repo AND digest = @digest"
            addParam cmd "@repo" repo
            addParam cmd "@digest" dg
            use! r = cmd.ExecuteReaderAsync()
            let! has = r.ReadAsync()

            return
                if has then
                    Some
                        { Digest = r.GetString 0
                          ContentType = r.GetString 1
                          Size = r.GetInt64 2 }
                else
                    None
    }

let deleteManifest (db: Db) (repo: string) (reference: string) : Task<unit> =
    task {
        if reference.StartsWith "sha256:" then
            do! execNonQuery db "DELETE FROM manifests WHERE repo = @repo AND digest = @ref" [ "@repo", box repo; "@ref", box reference ]
            do! execNonQuery db "DELETE FROM tags WHERE repo = @repo AND digest = @ref" [ "@repo", box repo; "@ref", box reference ]
        else
            do! execNonQuery db "DELETE FROM tags WHERE repo = @repo AND tag = @ref" [ "@repo", box repo; "@ref", box reference ]
    }

// ── Tags ──────────────────────────────────────────────────────────────────────

let putTag (db: Db) (repo: string) (tag: string) (digest: string) : Task<unit> =
    execNonQuery
        db
        "INSERT INTO tags (repo, tag, digest, created) VALUES (@repo, @tag, @digest, @created)
         ON CONFLICT (repo, tag) DO UPDATE SET digest = excluded.digest"
        [ "@repo", box repo; "@tag", box tag; "@digest", box digest; "@created", box (nowSecs ()) ]

let listTags (db: Db) (repo: string) : Task<string list> =
    task {
        use conn = newConn db
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT tag FROM tags WHERE repo = @repo ORDER BY tag"
        addParam cmd "@repo" repo
        use! r = cmd.ExecuteReaderAsync()
        let acc = ResizeArray<string>()
        let mutable go = true
        while go do
            let! has = r.ReadAsync()
            if has then acc.Add(r.GetString 0) else go <- false
        return List.ofSeq acc
    }

let listRepos (db: Db) : Task<string list> =
    task {
        use conn = newConn db
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT DISTINCT repo FROM manifests ORDER BY repo"
        use! r = cmd.ExecuteReaderAsync()
        let acc = ResizeArray<string>()
        let mutable go = true
        while go do
            let! has = r.ReadAsync()
            if has then acc.Add(r.GetString 0) else go <- false
        return List.ofSeq acc
    }

// ── In-progress uploads ───────────────────────────────────────────────────────

let createUpload (db: Db) (uuid: string) (repo: string) : Task<unit> =
    execNonQuery
        db
        "INSERT INTO uploads (uuid, repo, \"offset\", created) VALUES (@uuid, @repo, 0, @created)"
        [ "@uuid", box uuid; "@repo", box repo; "@created", box (nowSecs ()) ]

let getUpload (db: Db) (uuid: string) : Task<UploadRow option> =
    task {
        use conn = newConn db
        do! conn.OpenAsync()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT repo, \"offset\", digest, s3_upload_id, parts FROM uploads WHERE uuid = @uuid"
        addParam cmd "@uuid" uuid
        use! r = cmd.ExecuteReaderAsync()
        let! has = r.ReadAsync()

        return
            if has then
                Some
                    { Repo = r.GetString 0
                      Offset = r.GetInt64 1
                      Digest = getStrOpt r 2
                      S3UploadId = getStrOpt r 3
                      Parts = getStrOpt r 4 }
            else
                None
    }

/// Persist resumable-multipart state (S3 upload id, parts JSON, offset, digest).
let updateUploadMpu (db: Db) (uuid: string) (s3UploadId: string) (partsJson: string) (offset: int64) (digest: string option) : Task<unit> =
    execNonQuery
        db
        "UPDATE uploads SET s3_upload_id = @sid, parts = @parts, \"offset\" = @off, digest = @digest WHERE uuid = @uuid"
        [ "@sid", box s3UploadId
          "@parts", box partsJson
          "@off", box offset
          "@digest", optObj digest
          "@uuid", box uuid ]

let deleteUpload (db: Db) (uuid: string) : Task<unit> =
    execNonQuery db "DELETE FROM uploads WHERE uuid = @uuid" [ "@uuid", box uuid ]
