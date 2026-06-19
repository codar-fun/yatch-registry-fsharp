module Yatch.Config

open System

/// Configuration loaded from environment variables (mirrors the env contract of
/// the original Rust yatch so the same Nomad jobs / Docker env work unchanged).
type Config =
    { Host: string
      Port: int
      S3Bucket: string
      S3Region: string
      S3Endpoint: string option
      S3PublicUrl: string option
      /// Force path-style addressing. Defaults to true when a custom endpoint is
      /// set (R2/MinIO); set false for Aliyun OSS (virtual-hosted).
      S3ForcePathStyle: bool option
      PresignTtlSecs: int
      DbPath: string
      DatabaseUrl: string option
      AuthToken: string option
      AuthUser: string option
      LogLevel: string }

let private env (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null -> None
    | "" -> None
    | v -> Some v

let private envOr name d = env name |> Option.defaultValue d

let private envBool name =
    env name |> Option.map (fun v -> v.Trim().ToLowerInvariant() = "true")

let load () : Config =
    { Host = envOr "HOST" "0.0.0.0"
      Port = env "PORT" |> Option.map int |> Option.defaultValue 5000
      S3Bucket =
        match env "S3_BUCKET" with
        | Some b -> b
        | None -> failwith "S3_BUCKET environment variable is required"
      S3Region = envOr "S3_REGION" "us-east-1"
      S3Endpoint = env "S3_ENDPOINT"
      S3PublicUrl = env "S3_PUBLIC_URL"
      S3ForcePathStyle = envBool "S3_FORCE_PATH_STYLE"
      PresignTtlSecs = env "PRESIGN_TTL_SECS" |> Option.map int |> Option.defaultValue 3600
      DbPath = envOr "DB_PATH" "./yatch.db"
      DatabaseUrl = env "DATABASE_URL"
      AuthToken = env "AUTH_TOKEN"
      AuthUser = env "AUTH_USER"
      LogLevel = envOr "LOG_LEVEL" "info" }
