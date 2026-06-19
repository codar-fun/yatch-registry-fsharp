module Yatch.Auth

open System
open System.Text
open Yatch.Config

let private decodeBasic (b64: string) : (string * string) option =
    try
        let s = Encoding.UTF8.GetString(Convert.FromBase64String b64)
        match s.IndexOf ':' with
        | -1 -> None
        | i -> Some(s.Substring(0, i), s.Substring(i + 1))
    with _ ->
        None

/// True when the request is authorized. Auth is disabled when AuthToken is None.
/// Accepts `Bearer <token>` or HTTP Basic where the password equals the token
/// (and username matches AuthUser when set) — so stock `docker login` works.
let check (cfg: Config) (authHeader: string option) : bool =
    match cfg.AuthToken with
    | None -> true
    | Some expected ->
        match authHeader with
        | None -> false
        | Some h when h.StartsWith "Bearer " -> h.Substring("Bearer ".Length) = expected
        | Some h when h.StartsWith "Basic " ->
            match decodeBasic (h.Substring("Basic ".Length)) with
            | Some(user, pass) ->
                let userOk =
                    match cfg.AuthUser with
                    | Some u -> u = user
                    | None -> true
                userOk && pass = expected
            | None -> false
        | Some _ -> false
