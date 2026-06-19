module Yatch.Handlers

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Yatch
open Yatch.Config

type AppState =
    { Cfg: Config
      Db: Db.Db
      S3: Storage.S3Store
      Progress: Progress.Registry }

let private apiVersionHeader = "registry/2.0"

// ── Response helpers ──────────────────────────────────────────────────────────

let private hdr (ctx: HttpContext) (k: string) (v: string) =
    ctx.Response.Headers.[k] <- StringValues v

let private writeJson (ctx: HttpContext) (code: int) (body: string) : Task =
    ctx.Response.StatusCode <- code
    ctx.Response.ContentType <- "application/json"
    ctx.Response.WriteAsync body

let private ociErr (ctx: HttpContext) (code: int) (body: string) : Task = writeJson ctx code body

let private empty (ctx: HttpContext) (code: int) : Task =
    ctx.Response.StatusCode <- code
    Task.CompletedTask

let private methodNotAllowed (ctx: HttpContext) : Task = empty ctx 405
let private notFound (ctx: HttpContext) (msg: string) : Task = ociErr ctx 404 (Oci.errorJson "NOT_FOUND" msg)
let private badRequest (ctx: HttpContext) (msg: string) : Task = ociErr ctx 400 (Oci.errorJson "UNSUPPORTED" msg)
let private internalErr (ctx: HttpContext) (msg: string) : Task = ociErr ctx 500 (Oci.errorJson "INTERNAL_ERROR" msg)

let private header (ctx: HttpContext) (k: string) : string option =
    match ctx.Request.Headers.TryGetValue k with
    | true, v -> Some(v.ToString())
    | _ -> None

let private query (ctx: HttpContext) (k: string) : string option =
    match ctx.Request.Query.TryGetValue k with
    | true, v -> Some(v.ToString())
    | _ -> None

let private contentLength (ctx: HttpContext) : int64 =
    if ctx.Request.ContentLength.HasValue then ctx.Request.ContentLength.Value else 0L

let private hasBody (ctx: HttpContext) = contentLength ctx > 0L

let private contentRangeStart (ctx: HttpContext) : int64 option =
    match header ctx "Content-Range" with
    | Some raw ->
        let s = raw.Trim()
        let s = if s.StartsWith "bytes " then s.Substring 6 else s
        let first = s.Split([| '-'; '/' |]).[0].Trim()
        match Int64.TryParse first with
        | true, n -> Some n
        | _ -> None
    | None -> None

let private readAllBytes (stream: Stream) : Task<byte[]> =
    task {
        use ms = new MemoryStream()
        do! stream.CopyToAsync ms
        return ms.ToArray()
    }

let private serializeParts (parts: Storage.PartMeta list) = JsonSerializer.Serialize(List.toArray parts)

let private deserializeParts (s: string option) : Storage.PartMeta list =
    match s with
    | Some j when j <> "" ->
        try
            JsonSerializer.Deserialize<Storage.PartMeta[]> j |> List.ofArray
        with _ ->
            []
    | _ -> []

// ── Top-level endpoints ───────────────────────────────────────────────────────

let versionCheck (ctx: HttpContext) : Task =
    hdr ctx "Docker-Distribution-API-Version" apiVersionHeader
    writeJson ctx 200 "{}"

let catalog (st: AppState) (ctx: HttpContext) : Task =
    task {
        let! repos = Db.listRepos st.Db
        let body = JsonSerializer.Serialize {| repositories = List.toArray repos |}
        do! writeJson ctx 200 body
    }

let progress (st: AppState) (ctx: HttpContext) : Task =
    let items =
        st.Progress.List()
        |> List.map (fun s ->
            {| uuid = s.Uuid
               repo = s.Repo
               bytes = s.Bytes
               total = s.Total
               elapsed_secs = Math.Round(s.ElapsedSecs, 2)
               avg_bps = s.AvgBps
               recent_bps = s.RecentBps
               percent =
                if s.Total > 0L then
                    Math.Round(float s.Bytes / float s.Total * 100.0, 1)
                else
                    0.0 |})
        |> List.toArray

    writeJson ctx 200 (JsonSerializer.Serialize {| uploads = items |})

let authChallenge (ctx: HttpContext) : Task =
    hdr ctx "WWW-Authenticate" "Basic realm=\"yatch\""
    ociErr ctx 401 Oci.unauthorized

// ── Manifests ─────────────────────────────────────────────────────────────────

let private getManifest (st: AppState) (ctx: HttpContext) (repo: string) (reference: string) : Task =
    task {
        match! Db.getManifest st.Db repo reference with
        | None -> return! ociErr ctx 404 Oci.manifestUnknown
        | Some m ->
            match! st.S3.Get(Oci.manifestKey repo m.Digest) with
            | Some(data, _) ->
                ctx.Response.StatusCode <- 200
                ctx.Response.ContentType <- m.ContentType
                hdr ctx "Docker-Content-Digest" m.Digest
                hdr ctx "Docker-Distribution-API-Version" apiVersionHeader
                ctx.Response.ContentLength <- Nullable(int64 data.Length)
                do! ctx.Response.Body.WriteAsync(data, 0, data.Length)
            | None -> return! ociErr ctx 404 Oci.manifestUnknown
    }

let private headManifest (st: AppState) (ctx: HttpContext) (repo: string) (reference: string) : Task =
    task {
        match! Db.getManifest st.Db repo reference with
        | None -> return! ociErr ctx 404 Oci.manifestUnknown
        | Some m ->
            ctx.Response.StatusCode <- 200
            ctx.Response.ContentType <- m.ContentType
            hdr ctx "Docker-Content-Digest" m.Digest
            hdr ctx "Docker-Distribution-API-Version" apiVersionHeader
            ctx.Response.ContentLength <- Nullable m.Size
            return ()
    }

let private putManifest (st: AppState) (ctx: HttpContext) (repo: string) (reference: string) : Task =
    task {
        let! body = readAllBytes ctx.Request.Body
        let ct = header ctx "Content-Type" |> Option.defaultValue "application/vnd.docker.distribution.manifest.v2+json"
        let digest = Oci.computeDigest body
        let key = Oci.manifestKey repo digest
        do! st.S3.Put(key, body, ct)
        do! Db.putManifest st.Db repo digest ct (int64 body.Length)
        if not (reference.StartsWith "sha256:") then
            do! Db.putTag st.Db repo reference digest
        ctx.Response.StatusCode <- 201
        hdr ctx "Docker-Content-Digest" digest
        hdr ctx "Location" (sprintf "/v2/%s/manifests/%s" repo digest)
        return ()
    }

let private deleteManifest (st: AppState) (ctx: HttpContext) (repo: string) (reference: string) : Task =
    task {
        match! Db.getManifest st.Db repo reference with
        | None -> return! ociErr ctx 404 Oci.manifestUnknown
        | Some m ->
            do! st.S3.Delete(Oci.manifestKey repo m.Digest) // best effort
            do! Db.deleteManifest st.Db repo reference
            return! empty ctx 202
    }

// ── Blobs ─────────────────────────────────────────────────────────────────────

let private getBlob (st: AppState) (ctx: HttpContext) (digest: string) : Task =
    task {
        let key = Oci.blobKey digest
        match! st.S3.Head key with
        | None -> return! ociErr ctx 404 Oci.blobUnknown
        | Some _ ->
            ctx.Response.StatusCode <- 307
            hdr ctx "Location" (st.S3.BlobUrl key)
            hdr ctx "Docker-Content-Digest" digest
            return ()
    }

let private headBlob (st: AppState) (ctx: HttpContext) (digest: string) : Task =
    task {
        match! st.S3.Head(Oci.blobKey digest) with
        | None -> return! ociErr ctx 404 Oci.blobUnknown
        | Some size ->
            ctx.Response.StatusCode <- 200
            hdr ctx "Docker-Content-Digest" digest
            ctx.Response.ContentType <- "application/octet-stream"
            ctx.Response.ContentLength <- Nullable size
            return ()
    }

let private deleteBlob (st: AppState) (ctx: HttpContext) (digest: string) : Task =
    task {
        let key = Oci.blobKey digest
        match! st.S3.Head key with
        | None -> return! ociErr ctx 404 Oci.blobUnknown
        | Some _ ->
            do! st.S3.Delete key
            return! empty ctx 202
    }

// ── Blob uploads ──────────────────────────────────────────────────────────────

let private initiateUpload (st: AppState) (ctx: HttpContext) (repo: string) : Task =
    task {
        let mutable mounted = false
        match query ctx "mount" with
        | Some digest ->
            match! st.S3.Head(Oci.blobKey digest) with
            | Some _ ->
                ctx.Response.StatusCode <- 201
                hdr ctx "Docker-Content-Digest" digest
                hdr ctx "Location" (sprintf "/v2/%s/blobs/%s" repo digest)
                mounted <- true
            | None -> ()
        | None -> ()

        if not mounted then
            let uuid = Guid.NewGuid().ToString()
            do! Db.createUpload st.Db uuid repo
            ctx.Response.StatusCode <- 202
            hdr ctx "Docker-Upload-UUID" uuid
            hdr ctx "Location" (sprintf "/v2/%s/blobs/uploads/%s" repo uuid)
            hdr ctx "Range" "0-0"
    }

let private uploadStatus (st: AppState) (ctx: HttpContext) (repo: string) (uuid: string) : Task =
    task {
        match! Db.getUpload st.Db uuid with
        | None -> return! notFound ctx "upload not found"
        | Some u ->
            ctx.Response.StatusCode <- 204
            hdr ctx "Docker-Upload-UUID" uuid
            hdr ctx "Location" (sprintf "/v2/%s/blobs/uploads/%s" repo uuid)
            hdr ctx "Range" (sprintf "0-%d" (max 0L (u.Offset - 1L)))
            return ()
    }

let private patchUpload (st: AppState) (ctx: HttpContext) (repo: string) (uuid: string) : Task =
    task {
        match! Db.getUpload st.Db uuid with
        | None -> return! notFound ctx "upload not found"
        | Some u ->
            let key = Oci.uploadKey uuid
            let rangeStart = contentRangeStart ctx
            let restart = rangeStart = Some 0L && u.Offset > 0L
            let fresh = u.S3UploadId.IsNone || restart

            // A continuation chunk must start exactly at the current offset.
            let contiguous =
                fresh
                || (match rangeStart with
                    | Some s -> s = u.Offset
                    | None -> true)

            if not contiguous then
                return! ociErr ctx 416 (Oci.errorJson "RANGE_INVALID" "chunk start does not match current offset")
            else
                if restart then
                    match u.S3UploadId with
                    | Some old -> do! st.S3.AbortMpu(key, old)
                    | None -> ()

                let! uploadId, existingParts, baseOffset =
                    task {
                        if fresh then
                            let! id = st.S3.CreateMpu key
                            return id, [], 0L
                        else
                            return u.S3UploadId.Value, deserializeParts u.Parts, u.Offset
                    }

                let startPart =
                    match existingParts with
                    | [] -> 1
                    | ps -> (ps |> List.map (fun p -> p.Number) |> List.max) + 1

                let tracker = st.Progress.Start(uuid, repo, contentLength ctx)
                try
                    let! newParts, bytes, chunkSha = st.S3.AppendStream(key, uploadId, startPart, ctx.Request.Body, Some tracker)
                    let allParts = existingParts @ newParts
                    let newOffset = baseOffset + bytes
                    let singlePass = fresh && baseOffset = 0L
                    let storedDigest = if singlePass then Some chunkSha else None
                    do! Db.updateUploadMpu st.Db uuid uploadId (serializeParts allParts) newOffset storedDigest
                    ctx.Response.StatusCode <- 202
                    hdr ctx "Docker-Upload-UUID" uuid
                    hdr ctx "Location" (sprintf "/v2/%s/blobs/uploads/%s" repo uuid)
                    hdr ctx "Range" (sprintf "0-%d" (max 0L (newOffset - 1L)))
                    return ()
                finally
                    st.Progress.Finish uuid
    }

let private created201 (ctx: HttpContext) (repo: string) (digest: string) =
    ctx.Response.StatusCode <- 201
    hdr ctx "Docker-Content-Digest" digest
    hdr ctx "Location" (sprintf "/v2/%s/blobs/%s" repo digest)

/// PUT with a body — whole blob streamed in the finalize request.
let private completeUploadStreamed (st: AppState) (ctx: HttpContext) (repo: string) (uuid: string) : Task =
    task {
        match query ctx "digest" with
        | None -> return! badRequest ctx "digest query parameter required"
        | Some expected ->
            match! Db.getUpload st.Db uuid with
            | None -> return! notFound ctx "upload not found"
            | Some _ ->
                let dstKey = Oci.blobKey expected
                // Idempotent re-push: blob already present.
                match! st.S3.Head dstKey with
                | Some _ ->
                    do! Db.deleteUpload st.Db uuid
                    created201 ctx repo expected
                    return ()
                | None ->
                    let tmpKey = Oci.uploadKey uuid
                    let tracker = st.Progress.Start(uuid, repo, contentLength ctx)
                    let! computed =
                        task {
                            try
                                let! _, d = st.S3.PutMultipart(tmpKey, ctx.Request.Body, Some tracker)
                                return Ok d
                            with ex -> return Error(ex.Message)
                            }
                    st.Progress.Finish uuid
                    match computed with
                    | Error e -> return! internalErr ctx e
                    | Ok d when d <> expected ->
                        do! st.S3.Delete tmpKey
                        return! ociErr ctx 400 Oci.digestInvalid
                    | Ok _ ->
                        do! st.S3.Copy(tmpKey, dstKey)
                        do! st.S3.Delete tmpKey
                        do! Db.deleteUpload st.Db uuid
                        created201 ctx repo expected
                        return ()
    }

/// Empty PUT — finalize a prior PATCH multipart session.
let private completeUpload (st: AppState) (ctx: HttpContext) (repo: string) (uuid: string) : Task =
    task {
        match query ctx "digest" with
        | None -> return! badRequest ctx "digest query parameter required"
        | Some expected ->
            match! Db.getUpload st.Db uuid with
            | None -> return! notFound ctx "upload not found"
            | Some u ->
                let dstKey = Oci.blobKey expected
                let tmpKey = Oci.uploadKey uuid
                let inlineDigest = u.Digest |> Option.filter (fun d -> d <> "")

                match u.S3UploadId with
                | Some uploadId ->
                    let parts = deserializeParts u.Parts
                    if List.isEmpty parts then
                        return! badRequest ctx "no data uploaded for this session"
                    else
                        do! st.S3.CompleteMpu(tmpKey, uploadId, parts)
                        let! actual =
                            task {
                                match inlineDigest with
                                | Some d -> return d
                                | None ->
                                    let! _, d = st.S3.HashObject tmpKey
                                    return d
                            }
                        if actual <> expected then
                            do! st.S3.Delete tmpKey
                            return! ociErr ctx 400 Oci.digestInvalid
                        else
                            do! st.S3.Copy(tmpKey, dstKey)
                            do! st.S3.Delete tmpKey
                            do! Db.deleteUpload st.Db uuid
                            created201 ctx repo expected
                            return ()
                | None ->
                    // Legacy fallback: verify by reading temp object.
                    match! st.S3.Get tmpKey with
                    | Some(data, _) when Oci.verifyDigest data expected ->
                        do! st.S3.Put(dstKey, data, "application/octet-stream")
                        do! st.S3.Delete tmpKey
                        do! Db.deleteUpload st.Db uuid
                        created201 ctx repo expected
                        return ()
                    | Some _ -> return! ociErr ctx 400 Oci.digestInvalid
                    | None -> return! badRequest ctx "no blob data found for upload"
    }

let private cancelUpload (st: AppState) (ctx: HttpContext) (uuid: string) : Task =
    task {
        do! st.S3.Delete(Oci.uploadKey uuid)
        do! Db.deleteUpload st.Db uuid
        return! empty ctx 204
    }

let private listTags (st: AppState) (ctx: HttpContext) (repo: string) : Task =
    task {
        let! tags = Db.listTags st.Db repo
        do! writeJson ctx 200 (JsonSerializer.Serialize {| name = repo; tags = List.toArray tags |})
    }

// ── Dispatcher ────────────────────────────────────────────────────────────────

let private dispatch (st: AppState) (ctx: HttpContext) (path: string) : Task =
    let m = ctx.Request.Method
    match Oci.parse path with
    | Some(Oci.Manifests(name, reference)) ->
        match m with
        | "GET" -> getManifest st ctx name reference
        | "HEAD" -> headManifest st ctx name reference
        | "PUT" -> putManifest st ctx name reference
        | "DELETE" -> deleteManifest st ctx name reference
        | _ -> methodNotAllowed ctx
    | Some(Oci.Blob(_, digest)) ->
        match m with
        | "GET" -> getBlob st ctx digest
        | "HEAD" -> headBlob st ctx digest
        | "DELETE" -> deleteBlob st ctx digest
        | _ -> methodNotAllowed ctx
    | Some(Oci.BlobUploadStart name) -> if m = "POST" then initiateUpload st ctx name else methodNotAllowed ctx
    | Some(Oci.BlobUpload(name, uuid)) ->
        match m with
        | "GET" -> uploadStatus st ctx name uuid
        | "PATCH" -> patchUpload st ctx name uuid
        | "PUT" -> if hasBody ctx then completeUploadStreamed st ctx name uuid else completeUpload st ctx name uuid
        | "DELETE" -> cancelUpload st ctx uuid
        | _ -> methodNotAllowed ctx
    | Some(Oci.TagsList name) -> if m = "GET" then listTags st ctx name else methodNotAllowed ctx
    | None -> notFound ctx "path not found"

/// Single entry point routed from a terminal middleware.
let handle (st: AppState) (ctx: HttpContext) : Task =
    let path = ctx.Request.Path.Value
    let m = ctx.Request.Method

    if path = "/_progress" && m = "GET" then progress st ctx
    elif (path = "/v2/" || path = "/v2") && m = "GET" then versionCheck ctx
    elif path = "/v2/_catalog" && m = "GET" then catalog st ctx
    elif path.StartsWith "/v2/" then
        if st.Cfg.AuthToken.IsSome && not (Auth.check st.Cfg (header ctx "Authorization")) then
            authChallenge ctx
        else
            dispatch st ctx (path.Substring "/v2/".Length)
    else
        notFound ctx "not found"
