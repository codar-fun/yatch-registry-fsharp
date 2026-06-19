module Yatch.Oci

open System.Text

/// A parsed OCI Distribution path (everything after `/v2/`). Repository names are
/// multi-segment, so parsing keys off substring markers — identical to the Rust
/// yatch so the same OSS layout and DB rows are interpreted the same way.
type OciPath =
    | Manifests of name: string * reference: string
    | Blob of name: string * digest: string
    | BlobUploadStart of name: string
    | BlobUpload of name: string * uuid: string
    | TagsList of name: string

/// Parse the path segment after `/v2/`. `/manifests/` and `/blobs/uploads` are
/// checked before the generic `/blobs/` so they aren't read as a digest.
let parse (rawPath: string) : OciPath option =
    let path = rawPath.TrimStart('/')

    let tryManifests () =
        let i = path.IndexOf("/manifests/")
        if i >= 0 then
            let name = path.Substring(0, i)
            let reference = path.Substring(i + "/manifests/".Length)
            if name <> "" && reference <> "" then Some(Manifests(name, reference)) else None
        else
            None

    let tryUploads () =
        let i = path.IndexOf("/blobs/uploads")
        if i >= 0 then
            let name = path.Substring(0, i)
            let rest = path.Substring(i + "/blobs/uploads".Length).TrimStart('/')
            if name <> "" then
                if rest = "" then Some(BlobUploadStart name) else Some(BlobUpload(name, rest))
            else
                None
        else
            None

    let tryBlob () =
        let i = path.IndexOf("/blobs/")
        if i >= 0 then
            let name = path.Substring(0, i)
            let digest = path.Substring(i + "/blobs/".Length)
            if name <> "" && digest <> "" then Some(Blob(name, digest)) else None
        else
            None

    let tryTags () =
        if path.EndsWith("/tags/list") then
            let name = path.Substring(0, path.Length - "/tags/list".Length)
            if name <> "" then Some(TagsList name) else None
        else
            None

    tryManifests ()
    |> Option.orElseWith tryUploads
    |> Option.orElseWith tryBlob
    |> Option.orElseWith tryTags

// ── OCI error payloads ────────────────────────────────────────────────────────

let private jsonEscape (s: string) =
    let sb = StringBuilder()
    for c in s do
        match c with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c -> sb.Append c |> ignore
    sb.ToString()

let errorJson (code: string) (message: string) =
    sprintf """{"errors":[{"code":"%s","message":"%s"}]}""" code (jsonEscape message)

let manifestUnknown = errorJson "MANIFEST_UNKNOWN" "manifest unknown"
let blobUnknown = errorJson "BLOB_UNKNOWN" "blob unknown"
let digestInvalid = errorJson "DIGEST_INVALID" "provided digest did not match uploaded content"
let unauthorized = errorJson "UNAUTHORIZED" "authentication required"

// ── Storage key helpers (match the Rust yatch-core layout exactly) ─────────────

/// Content-addressed blob key, deduped across all repos.
let blobKey (digest: string) = sprintf "blobs/%s" digest

/// Per-repo manifest key.
let manifestKey (repo: string) (digest: string) = sprintf "manifests/%s/%s" repo digest

/// Temporary upload key, deleted after the upload completes.
let uploadKey (uuid: string) = sprintf "uploads/%s" uuid

// ── Digest utilities ──────────────────────────────────────────────────────────

let computeDigest (data: byte[]) =
    use sha = System.Security.Cryptography.SHA256.Create()
    let hash = sha.ComputeHash data
    "sha256:" + (hash |> Array.map (sprintf "%02x") |> String.concat "")

let verifyDigest (data: byte[]) (expected: string) = computeDigest data = expected
