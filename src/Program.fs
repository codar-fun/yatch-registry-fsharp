module Yatch.Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Yatch
open Yatch.Handlers

[<EntryPoint>]
let main argv =
    let cfg = Config.load ()

    // Open + migrate the metadata store (SQLite file, or shared Postgres).
    let db = Db.openDb cfg.DatabaseUrl cfg.DbPath
    (Db.migrate db).GetAwaiter().GetResult()

    let s3 = Storage.S3Store cfg
    let progress = Progress.Registry()

    let state =
        { Cfg = cfg
          Db = db
          S3 = s3
          Progress = progress }

    let builder = WebApplication.CreateBuilder(argv)

    // Allow arbitrarily large layer uploads; stream them (never buffer wholesale).
    builder.WebHost.ConfigureKestrel(fun (o: KestrelServerOptions) ->
        o.Limits.MaxRequestBodySize <- Nullable()
        o.AllowSynchronousIO <- false)
    |> ignore

    builder.WebHost.UseUrls(sprintf "http://%s:%d" cfg.Host cfg.Port) |> ignore

    let app = builder.Build()

    // Single terminal middleware dispatches every request (OCI + /_progress).
    app.Run(fun ctx -> handle state ctx)

    let backend =
        match db with
        | Db.Postgres _ -> "postgres"
        | Db.Sqlite _ -> sprintf "sqlite(%s)" cfg.DbPath
    printfn "Yatch (F#) registry on %s:%d | metadata=%s | bucket=%s | endpoint=%s | auth=%b"
        cfg.Host cfg.Port backend cfg.S3Bucket
        (cfg.S3Endpoint |> Option.defaultValue "(default)")
        cfg.AuthToken.IsSome
    app.Run()
    0
