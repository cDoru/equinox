﻿/// Internal data stuctures. While these are intended to be legible, understanding the abstractions involved is only necessary if you are implemening a Store or a decorator thereof.
/// i.e., if you're seeking to undertand the core deliverables of the Equinox library, that's in Equinox.fs, not here
namespace Equinox.Store

open Serilog
open System

/// Store-specific opaque token to be used for synchronization purposes
[<NoComparison>]
type StreamToken = { value : obj }

/// Internal type used to represent the outcome of a TrySync operation
[<NoEquality; NoComparison; RequireQualifiedAccess>]
type SyncResult<'state> =
    /// The write succeeded (the supplied token and state can be used to efficiently continue the processing iff desired)
    | Written of StreamToken * 'state
    /// The set of changes supplied to TrySync conflict with the present state of the underlying stream based on the configured policy for that store
    /// The inner is Async as some stores (and/or states) are such that determining the conflicting state (iff required) needs an extra trip to obtain
    | Conflict of Async<StreamToken * 'state>

/// Store-agnostic interface representing interactions an Application can have with a set of streams with a common event type
type ICategory<'event, 'state> =
    /// Obtain the state from the target stream
    abstract Load : streamName: string -> log: ILogger
        -> Async<StreamToken * 'state>
    /// Given the supplied `token`, attempt to sync to the proposed updated `state'` by appending the supplied `events` to the underlying stream, yielding:
    /// - Written: signifies synchronization has succeeded, implying the included StreamState should now be assumed to be the state of the stream
    /// - Conflict: signifies the synch failed, and the proposed decision hence needs to be reconsidered in light of the supplied conflicting Stream State
    /// NB the central precondition upon which the sync is predicated is that the stream has not diverged from the `originState` represented by `token`
    ///    where the precondition is not met, the SyncResult.Conflict bears a [lazy] async result (in a specific manner optimal for the store)
    abstract TrySync : log: ILogger
        -> token: StreamToken * originState: 'state
        -> events: 'event list
        -> Async<SyncResult<'state>>

/// Store-agnostic interface representing interactions a Flow can have with the state of a given event stream. Not intended for direct use by consumer code.
type IStream<'event, 'state> =
    /// Obtain the state from the target stream
    abstract Load: log: ILogger
        -> Async<StreamToken * 'state>
    /// Given the supplied `token` [and related `originState`], attempt to move to state `state'` by appending the supplied `events` to the underlying stream
    /// SyncResult.Written: implies the state is now the value represented by the Result's value
    /// SyncResult.Conflict: implies the `events` were not synced; if desired the consumer can use the included resync workflow in order to retry
    abstract TrySync: log: ILogger
        -> token: StreamToken * originState: 'state
        -> events: 'event list
        -> Async<SyncResult<'state>>

/// Represents a time measurement of a computation that includes stopwatch tick metadata
[<NoEquality; NoComparison>]
type StopwatchInterval (startTicks : int64, endTicks : int64) =
    do if startTicks < 0L || startTicks > endTicks then invalidArg "ticks" "tick arguments do not form a valid interval."

    // Converts a tick count as measured by stopwatch into a TimeSpan value
    let timeSpanFromStopwatchTicks (ticks : int64) =
        let ticksPerSecond = double System.Diagnostics.Stopwatch.Frequency
        let totalSeconds = double ticks / ticksPerSecond
        TimeSpan.FromSeconds totalSeconds

    member __.StartTicks = startTicks
    member __.EndTicks = endTicks
    member __.Elapsed = timeSpanFromStopwatchTicks(endTicks - startTicks)
    override __.ToString () = let e = __.Elapsed in sprintf "%g ms" e.TotalMilliseconds

/// Low level stream builders, generally consumed via Store-specific Stream Builders that layer policies such as Caching in at the Category level
module Stream =
    /// Represents a specific stream in a ICategory
    type private Stream<'event, 'state>(category : ICategory<'event, 'state>, streamName) =
        interface IStream<'event, 'state> with
            member __.Load log =
                category.Load streamName log
            member __.TrySync (log: ILogger) (token: StreamToken, originState: 'state) (events: 'event list) =
                category.TrySync log (token, originState) events

    /// Handles case where some earlier processing has loaded or determined a the state of a stream, allowing us to avoid a read roundtrip
    type private InitializedStream<'event, 'state>(inner : IStream<'event, 'state>, memento : StreamToken * 'state) =
        let mutable preloadedTokenAndState = Some memento
        interface IStream<'event, 'state> with
            member __.Load log =
                match preloadedTokenAndState with
                | Some value -> async { preloadedTokenAndState <- None; return value }
                | None -> inner.Load log
            member __.TrySync (log: ILogger) (token: StreamToken, originState: 'state) (events: 'event list) =
                inner.TrySync log (token, originState) events

    let create (category : ICategory<'event, 'state>) streamName : IStream<'event, 'state> = Stream(category, streamName) :> _
    let ofMemento (memento : StreamToken * 'state) (inner : IStream<_,_>) : IStream<'event, 'state> = InitializedStream(inner, memento) :> _