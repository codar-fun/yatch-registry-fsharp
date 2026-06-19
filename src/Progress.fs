module Yatch.Progress

open System.Collections.Concurrent
open System.Diagnostics
open System.Threading

/// Realtime snapshot of an in-flight upload, returned by `GET /_progress`.
type Snapshot =
    { Uuid: string
      Repo: string
      Bytes: int64
      Total: int64
      ElapsedSecs: float
      AvgBps: int64
      RecentBps: int64 }

/// Tracks bytes streamed to object storage for a single upload, with a rolling
/// window for instantaneous speed. Thread-safe; updated from the upload path.
type Tracker(repo: string, totalHint: int64) =
    let sw = Stopwatch.StartNew()
    let mutable bytes = 0L
    let mutable winMs = 0L
    let mutable winBytes = 0L
    let mutable recentBps = 0L

    member _.Repo = repo
    member _.TotalHint = totalHint

    /// Record `n` bytes; refreshes the rolling-window rate roughly every 500ms.
    member _.Add(n: int64) =
        let total = Interlocked.Add(&bytes, n)
        let now = sw.ElapsedMilliseconds
        let dt = now - Volatile.Read(&winMs)
        if dt >= 500L then
            let bps = (total - Volatile.Read(&winBytes)) * 1000L / dt
            Volatile.Write(&recentBps, bps)
            Volatile.Write(&winMs, now)
            Volatile.Write(&winBytes, total)

    member _.Snapshot(uuid: string) =
        let b = Volatile.Read(&bytes)
        let secs = sw.Elapsed.TotalSeconds
        let avg = if secs > 0.0 then int64 (float b / secs) else 0L
        { Uuid = uuid
          Repo = repo
          Bytes = b
          Total = totalHint
          ElapsedSecs = secs
          AvgBps = avg
          RecentBps = Volatile.Read(&recentBps) }

/// Registry of active upload trackers, keyed by upload UUID.
type Registry() =
    let map = ConcurrentDictionary<string, Tracker>()

    member _.Start(uuid: string, repo: string, totalHint: int64) =
        let t = Tracker(repo, totalHint)
        map.[uuid] <- t
        t

    member _.Finish(uuid: string) = map.TryRemove(uuid) |> ignore

    member _.List() =
        [ for kv in map -> kv.Value.Snapshot(kv.Key) ]
