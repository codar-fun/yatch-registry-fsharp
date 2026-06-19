module Yatch.Storage

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Amazon.S3
open Amazon.S3.Model
open Yatch.Config

/// One finished part of a multipart upload — serialized to JSON in the
/// `uploads.parts` column so an interrupted push can resume across reconnects.
type PartMeta =
    { Number: int
      ETag: string
      Size: int64 }

let private partSize = 8 * 1024 * 1024 // 8 MB
let private maxConcurrentParts = 4
let private maxPartRetries = 5

let private toHex (bytes: byte[]) =
    bytes |> Array.map (sprintf "%02x") |> String.concat ""

let private backoffMs (attempt: int) =
    min 8000 (250 * (1 <<< (attempt - 1)))

/// S3 / Aliyun-OSS storage. Uploads stream to multipart with bounded concurrency
/// and per-part retry; socket + attempt timeouts ensure a stalled connection
/// aborts and retries instead of hanging.
type S3Store(cfg: Config) =
    let bucket = cfg.S3Bucket

    let client =
        let c = AmazonS3Config()
        c.ForcePathStyle <- defaultArg cfg.S3ForcePathStyle cfg.S3Endpoint.IsSome
        match cfg.S3Endpoint with
        | Some e -> c.ServiceURL <- e
        | None -> ()
        c.AuthenticationRegion <- cfg.S3Region
        c.MaxErrorRetry <- 8
        // Overall per-request backstop. Fine-grained per-attempt timeouts are
        // enforced with CancellationTokens (see withTimeout) because the SDK
        // ignores ReadWriteTimeout on async calls.
        c.Timeout <- Nullable(TimeSpan.FromSeconds 600.0)
        new AmazonS3Client(c)

    /// Run an S3 call under a hard per-attempt deadline so a stalled connection
    /// aborts (cancellation) and is retried, instead of hanging indefinitely.
    let withTimeout (seconds: float) (op: Threading.CancellationToken -> Task<'T>) : Task<'T> =
        task {
            use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds seconds)
            return! op cts.Token
        }

    // ── Simple object ops ──────────────────────────────────────────────────────

    member _.Put(key: string, data: byte[], contentType: string) : Task<unit> =
        task {
            use ms = new MemoryStream(data)
            let req = PutObjectRequest(BucketName = bucket, Key = key, ContentType = contentType, InputStream = ms)
            let! _ = withTimeout 120.0 (fun ct -> client.PutObjectAsync(req, ct))
            return ()
        }

    /// Returns the object bytes + content type, or None if absent.
    member _.Get(key: string) : Task<(byte[] * string) option> =
        task {
            try
                use! resp = withTimeout 60.0 (fun ct -> client.GetObjectAsync(GetObjectRequest(BucketName = bucket, Key = key), ct))
                use ms = new MemoryStream()
                do! resp.ResponseStream.CopyToAsync ms
                let ct = if isNull resp.Headers.ContentType then "application/octet-stream" else resp.Headers.ContentType
                return Some(ms.ToArray(), ct)
            with :? AmazonS3Exception as ex when ex.StatusCode = Net.HttpStatusCode.NotFound -> return None
        }

    /// Returns the object size, or None if absent.
    member _.Head(key: string) : Task<int64 option> =
        task {
            try
                let! resp = withTimeout 30.0 (fun ct -> client.GetObjectMetadataAsync(GetObjectMetadataRequest(BucketName = bucket, Key = key), ct))
                return Some resp.ContentLength
            with :? AmazonS3Exception as ex when ex.StatusCode = Net.HttpStatusCode.NotFound || ex.StatusCode = Net.HttpStatusCode.Forbidden -> return None
        }

    member _.Delete(key: string) : Task<unit> =
        task {
            let! _ = withTimeout 30.0 (fun ct -> client.DeleteObjectAsync(DeleteObjectRequest(BucketName = bucket, Key = key), ct))
            return ()
        }

    member _.Copy(srcKey: string, dstKey: string) : Task<unit> =
        task {
            let req = CopyObjectRequest(SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = dstKey)
            let! _ = withTimeout 300.0 (fun ct -> client.CopyObjectAsync(req, ct))
            return ()
        }

    /// A URL clients download blobs from directly — public URL or a presigned GET.
    member _.BlobUrl(key: string) : string =
        match cfg.S3PublicUrl with
        | Some baseUrl -> baseUrl.TrimEnd('/') + "/" + key
        | None ->
            let req =
                GetPreSignedUrlRequest(
                    BucketName = bucket,
                    Key = key,
                    Verb = HttpVerb.GET,
                    Expires = DateTime.UtcNow.AddSeconds(float cfg.PresignTtlSecs)
                )
            client.GetPreSignedURL req

    /// Stream an object through SHA-256 to recover its (size, digest). Used to
    /// verify a resumed upload at finalize. Memory-bounded.
    member _.HashObject(key: string) : Task<int64 * string> =
        task {
            use! resp = withTimeout 60.0 (fun ct -> client.GetObjectAsync(GetObjectRequest(BucketName = bucket, Key = key), ct))
            use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
            let buf = Array.zeroCreate (64 * 1024)
            let mutable total = 0L
            let mutable go = true
            while go do
                let! n = resp.ResponseStream.ReadAsync(buf, 0, buf.Length)
                if n <= 0 then
                    go <- false
                else
                    hash.AppendData(buf, 0, n)
                    total <- total + int64 n
            return total, "sha256:" + toHex (hash.GetHashAndReset())
        }

    // ── Multipart primitives ───────────────────────────────────────────────────

    member _.CreateMpu(key: string) : Task<string> =
        task {
            let req = InitiateMultipartUploadRequest(BucketName = bucket, Key = key, ContentType = "application/octet-stream")
            let! resp = withTimeout 30.0 (fun ct -> client.InitiateMultipartUploadAsync(req, ct))
            return resp.UploadId
        }

    member private _.UploadPartRetry(key: string, uploadId: string, partNumber: int, data: byte[]) : Task<string> =
        task {
            let mutable attempt = 0
            let mutable etag = None
            let mutable lastErr: exn = null
            while etag.IsNone && attempt < maxPartRetries do
                attempt <- attempt + 1
                try
                    use ms = new MemoryStream(data)
                    let req =
                        UploadPartRequest(
                            BucketName = bucket,
                            Key = key,
                            UploadId = uploadId,
                            PartNumber = partNumber,
                            PartSize = int64 data.Length,
                            InputStream = ms
                        )
                    let! resp = withTimeout 90.0 (fun ct -> client.UploadPartAsync(req, ct))
                    etag <- Some resp.ETag
                with ex ->
                    lastErr <- ex
                    if attempt < maxPartRetries then
                        do! Task.Delay(backoffMs attempt)
            match etag with
            | Some e -> return e
            | None -> return raise lastErr
        }

    member _.CompleteMpu(key: string, uploadId: string, parts: PartMeta list) : Task<unit> =
        task {
            let req = CompleteMultipartUploadRequest(BucketName = bucket, Key = key, UploadId = uploadId)
            req.PartETags <- System.Collections.Generic.List(parts |> List.map (fun p -> PartETag(p.Number, p.ETag)))
            let! _ = withTimeout 120.0 (fun ct -> client.CompleteMultipartUploadAsync(req, ct))
            return ()
        }

    member _.AbortMpu(key: string, uploadId: string) : Task<unit> =
        task {
            try
                let! _ = withTimeout 30.0 (fun ct -> client.AbortMultipartUploadAsync(AbortMultipartUploadRequest(BucketName = bucket, Key = key, UploadId = uploadId), ct))
                return ()
            with _ -> return ()
        }

    /// Stream a body into an existing multipart upload, emitting 8 MB parts with
    /// bounded concurrency (memory ~ maxConcurrentParts × partSize) and per-part
    /// retry. Hashes the stream in order. Returns (parts, bytes, sha-of-stream).
    member this.AppendStream
        (key: string, uploadId: string, startPartNumber: int, stream: Stream, tracker: Progress.Tracker option)
        : Task<PartMeta list * int64 * string> =
        task {
            use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
            use sem = new SemaphoreSlim(maxConcurrentParts)
            let tasks = ResizeArray<Task<PartMeta>>()
            let mutable partNumber = startPartNumber
            let mutable total = 0L
            use buf = new MemoryStream()
            let readBuf = Array.zeroCreate (256 * 1024)

            let startUpload (data: byte[]) (pn: int) =
                let t =
                    task {
                        try
                            let! etag = this.UploadPartRetry(key, uploadId, pn, data)
                            return { Number = pn; ETag = etag; Size = int64 data.Length }
                        finally
                            sem.Release() |> ignore
                    }
                tasks.Add t

            let mutable go = true
            while go do
                let! n = stream.ReadAsync(readBuf, 0, readBuf.Length)
                if n <= 0 then
                    go <- false
                else
                    hash.AppendData(readBuf, 0, n)
                    total <- total + int64 n
                    match tracker with
                    | Some t -> t.Add(int64 n)
                    | None -> ()
                    buf.Write(readBuf, 0, n)
                    if buf.Length >= int64 partSize then
                        let data = buf.ToArray()
                        buf.SetLength 0L
                        let pn = partNumber
                        partNumber <- partNumber + 1
                        // Block reading until a concurrency slot frees — bounds memory.
                        do! sem.WaitAsync()
                        startUpload data pn

            // Final (or only) part — any size.
            if buf.Length > 0L then
                let data = buf.ToArray()
                let pn = partNumber
                do! sem.WaitAsync()
                startUpload data pn

            let! parts = Task.WhenAll tasks
            let sorted = parts |> Array.sortBy (fun p -> p.Number) |> List.ofArray
            let sha = "sha256:" + toHex (hash.GetHashAndReset())
            return sorted, total, sha
        }

    /// Monolithic multipart upload (whole body in one call): create → append →
    /// complete. Returns (size, digest). Aborts the MPU on failure.
    member this.PutMultipart(key: string, stream: Stream, tracker: Progress.Tracker option) : Task<int64 * string> =
        task {
            let! uploadId = this.CreateMpu key
            try
                let! (parts, total, sha) = this.AppendStream(key, uploadId, 1, stream, tracker)
                do! this.CompleteMpu(key, uploadId, parts)
                return total, sha
            with ex ->
                do! this.AbortMpu(key, uploadId)
                return raise ex
        }
