﻿namespace Equinox

open Serilog

/// Maintains a rolling folded State while Accumulating Events decided upon as part of a decision flow
type Context<'event, 'state>(fold, originState : 'state) =
    let accumulated = ResizeArray<'event>()

    /// The current folded State, based on the Stream's `originState` + any events that have been Accumulated during the the decision flow
    member __.State =
        __.Accumulated |> fold originState
    /// Invoke a decision function, gathering the events (if any) that it decides are necessary into the `Accumulated` sequence
    member __.Execute (decide : 'state -> 'event list) : unit =
        decide __.State |> accumulated.AddRange
    /// Invoke an Async decision function, gathering the events (if any) that it decides are necessary into the `Accumulated` sequence
    member __.ExecuteAsync (decide : 'state -> Async<'event list>) : Async<unit> = async {
        let! events = decide __.State
        accumulated.AddRange events }
    /// As per `Execute`, invoke a decision function, while also propagating a result yielded as the fst of an (outcome, events) pair
    member __.Decide (decide : 'state -> 'result * 'event list) =
        let result, newEvents = decide __.State
        accumulated.AddRange newEvents
        result
    /// As per `ExecuteAsync`, invoke a decision function, while also propagating a result yielded as the fst of an (outcome, events) pair
    member __.DecideAsync (decide : 'state -> Async<'result * 'event list>) : Async<'result> = async {
        let! result, newEvents = decide __.State
        accumulated.AddRange newEvents
        return result }
    /// The Events that have thus far been pended via the `decide` functions `Execute`/`Decide`d during the course of this flow
    member __.Accumulated =
        accumulated |> List.ofSeq

/// Internal data stuctures. While these are intended to be legible, understanding the abstractions involved is only necessary if you are implemening a Store or a decorator thereof
[<RequireQualifiedAccess>]
module Storage =
    /// Store-specific opaque token to be used for synchronization purposes
    [<NoComparison>]
    type StreamToken = { value : obj }
    [<NoEquality; NoComparison; RequireQualifiedAccess>]
    type SyncResult<'state> =
        | Written of StreamToken * 'state
        | Conflict of Async<StreamToken * 'state>

/// Interface representing interactions a Flow can have with the state of a given event stream. Not intended for direct use by consumer code.
type IStream<'event, 'state> =
    /// Obtain the state from the target stream
    abstract Load: log: ILogger
        -> Async<Storage.StreamToken * 'state>
    /// Given the supplied `token` [and related `originState`], attempt to move to state `state'` by appending the supplied `events` to the underlying stream
    /// SyncResult.Written: implies the state is now the value represented by the Result's value
    /// SyncResult.Conflict: implies the `events` were not synced; if desired the consumer can use the included resync workflow in order to retry
    abstract TrySync: log: ILogger
        -> token: Storage.StreamToken * originState: 'state
        -> events: 'event list
        -> Async<Storage.SyncResult<'state>>

/// Store-agnostic interface representing interactions an Application can have with a set of streams
type ICategory<'event, 'state> =
    /// Obtain the state from the target stream
    abstract Load : streamName: string -> log: ILogger
        -> Async<Storage.StreamToken * 'state>
    /// Given the supplied `token`, attempt to sync to the proposed updated `state'` by appending the supplied `events` to the underlying stream, yielding:
    /// - Written: signifies synchronization has succeeded, implying the included StreamState should now be assumed to be the state of the stream
    /// - Conflict: signifies the synch failed, and the proposed decision hence needs to be reconsidered in light of the supplied conflicting Stream State
    /// NB the central precondition upon which the sync is predicated is that the stream has not diverged from the `originState` represented by `token`
    ///    where the precondition is not met, the SyncResult.Conflict bears a [lazy] async result (in a specific manner optimal for the store)
    abstract TrySync : log: ILogger
        -> token: Storage.StreamToken * originState: 'state
        -> events: 'event list
        -> Async<Storage.SyncResult<'state>>

/// Defines a hook enabling retry and backoff policies to be specified
type IRetryPolicy = abstract member Execute: log: ILogger * attemptNumber: int *  call: Async<'T> -> Async<'T>

// Exception yielded by Handler.Decide after `count` attempts have yielded conflicts at the point of syncing with the Store
exception MaxResyncsExhaustedException of count: int

/// Internal implementation of the Store agnostic load + run/render. See Handler for App-facing APIs.
module private Flow =
    /// Represents stream and folding state between the load and run/render phases
    type SyncState<'event, 'state>
        (   fold, originState : Storage.StreamToken * 'state,
            trySync : ILogger -> Storage.StreamToken * 'state -> 'event list -> Async<Storage.SyncResult<'state>>) =
        let mutable tokenAndState = originState
        let tryOr log events handleFailure = async {
            let! res = trySync log tokenAndState events
            match res with
            | Storage.SyncResult.Conflict resync ->
                return! handleFailure resync
            | Storage.SyncResult.Written (token', streamState') ->
                tokenAndState <- token', streamState'
                return true }

        member __.Memento = tokenAndState
        member __.State = snd __.Memento
        member __.CreateContext(): Context<'event, 'state> =
            Context<'event, 'state>(fold, __.State)
        member __.TryOrResync (retryPolicy : IRetryPolicy) (attemptNumber: int) (log : ILogger) events =
            let resyncInPreparationForRetry resync = async {
                let! streamState' = retryPolicy.Execute(log, attemptNumber, resync)
                tokenAndState <- streamState'
                return false }
            tryOr log events resyncInPreparationForRetry
        member __.TryOrThrow log events attempt =
            let throw _ = async { return raise <| MaxResyncsExhaustedException attempt }
            tryOr log events throw |> Async.Ignore

    /// Obtain a representation of the current state and metadata from the underlying storage stream
    let load (fold : 'state -> 'event seq -> 'state) (log : ILogger) (stream : IStream<'event, 'state>)
        : Async<SyncState<'event, 'state>> = async {
        let! streamState = stream.Load log
        return SyncState(fold, streamState, stream.TrySync) }

    /// Process a command, ensuring a consistent final state is established on the stream.
    /// 1.  make a decision predicated on the known state
    /// 2a. if no changes required, exit with known state
    /// 2b. if saved without conflict, exit with updated state
    /// 2b. if conflicting changes, retry by recommencing at step 1 with the updated state
    let run (sync : SyncState<'event, 'state>) (retryPolicy: IRetryPolicy, maxSyncAttempts : int) (log : ILogger) (decide : Context<'event, 'state> -> Async<'result * 'event list>)
        : Async<'result> =
        if maxSyncAttempts < 1 then raise <| System.ArgumentOutOfRangeException("maxSyncAttempts", maxSyncAttempts, "should be >= 1")
        /// Run a decision cycle - decide what events should be appended given the presented state
        let rec loop attempt: Async<'result> = async {
            let log = if attempt = 1 then log else log.ForContext("syncAttempt", attempt)
            let ctx = sync.CreateContext()
            let! outcome, events = decide ctx
            if List.isEmpty events then
                log.Debug "No events generated"
                return outcome
            elif attempt = maxSyncAttempts then
                log.Debug "Max Sync Attempts exceeded"
                do! sync.TryOrThrow log events attempt
                return outcome
            else
                let! committed = sync.TryOrResync retryPolicy attempt log events
                if not committed then
                    log.Debug "Resyncing and retrying"
                    return! loop (attempt + 1)
                else
                    return outcome }
        /// Commence, processing based on the incoming state
        loop 1

    /// Internal implementation providing a handler not associated with a specific log or stream
    /// Not 'just' making it public; the plan is to have Stream.Handler represent the public interface until further significant patterns present
    type HandlerImpl<'event, 'state>(fold, maxAttempts, retryPolicy) =
        let execAsync stream log f = async { let! syncState = load fold log stream in return! f syncState }
        let exec stream log f = execAsync stream log <| fun syncState -> async { return f syncState }
        let runFlow stream log decideAsync = execAsync stream log <| fun syncState -> async { return! run syncState (retryPolicy,maxAttempts) log decideAsync }

        member __.Decide(stream : IStream<'event, 'state>, log : ILogger, flow: Context<'event, 'state> -> 'result) : Async<'result> =
            runFlow stream log <| fun ctx -> async { let result = flow ctx in return result, ctx.Accumulated }
        member __.DecideAsync(stream : IStream<'event, 'state>, log : ILogger, flowAsync: Context<'event, 'state> -> Async<'result>): Async<'result> =
            runFlow stream log <| fun ctx -> async { let! result = flowAsync ctx in return result, ctx.Accumulated }
        member __.Query(stream : IStream<'event, 'state>, log : ILogger) : (SyncState<'event,'state> -> 'result) -> Async<'result> =
            exec stream log

module Stream =
    /// Represents a specific stream in an IStoreCategory
    type private Stream<'event, 'state>(category : ICategory<'event, 'state>, streamName) =
        interface IStream<'event, 'state> with
            member __.Load log =
                category.Load streamName log
            member __.TrySync (log: ILogger) (token: Storage.StreamToken, originState: 'state) (events: 'event list) =
                category.TrySync log (token, originState) events

    /// Handles case where some earlier processing has loaded or determined a the state of a stream, allowing us to avoid a read roundtrip
    type private InitializedStream<'event, 'state>(inner : IStream<'event, 'state>, memento : Storage.StreamToken * 'state) =
        let mutable preloadedTokenAndState = Some memento
        interface IStream<'event, 'state> with
            member __.Load log =
                match preloadedTokenAndState with
                | Some value -> async { preloadedTokenAndState <- None; return value }
                | None -> inner.Load log
            member __.TrySync (log: ILogger) (token: Storage.StreamToken, originState: 'state) (events: 'event list) =
                inner.TrySync log (token, originState) events

    let create (category : ICategory<'event, 'state>) streamName : IStream<'event, 'state> = Stream(category, streamName) :> _
    let ofMemento (memento : Storage.StreamToken * 'state) (x : IStream<_,_>) : IStream<'event, 'state> = InitializedStream(x, memento) :> _

/// Core Application-facing API. Wraps the handling of decision or query flow in a manner that is store agnostic
type Handler<'event, 'state>(fold, log, stream : IStream<'event, 'state>, maxAttempts : int, ?retryPolicy) =
    let retryPolicy = defaultArg retryPolicy ({ new IRetryPolicy with member __.Execute(_log, _attemptNumber, f) = async { return! f } })
    let inner = Flow.HandlerImpl<'event, 'state>(fold, maxAttempts, retryPolicy)

    /// 0. Invoke the supplied `decide` function 1. attempt to sync the accumulated events to the stream 2. (contigent on success of 1) yield the outcome.
    /// Tries up to `maxAttempts` times in the case of a conflict, throwing MaxResyncsExhaustedException` to signal failure.
    member __.Decide(flow : Context<'event, 'state> -> 'result) : Async<'result> =
        inner.Decide(stream,log,flow)
    /// 0. Invoke the supplied _Async_ `decide` function 1. attempt to sync the accumulated events to the stream 2. (contigent on success of 1) yield the outcome
    /// Tries up to `maxAttempts` times in the case of a conflict, throwing MaxResyncsExhaustedException` to signal failure.
    member __.DecideAsync(flowAsync : Context<'event, 'state> -> Async<'result>) : Async<'result> =
        inner.DecideAsync(stream,log,flowAsync)
    /// Low Level helper to allow one to obtain the complete state of a stream (including the position) in order to pass it within the application
    member __.Raw: Async<Storage.StreamToken * 'state> =
        inner.Query(stream,log) <| fun syncState -> syncState.Memento
    /// Project from the folded `State` without executing a decision flow as `Decide` does
    member __.Query(projection : 'state -> 'view) : Async<'view> =
        inner.Query(stream,log) <| fun syncState -> projection syncState.State

/// Exception yielded by ES Operation after `count` attempts to complete the operation have taken place
type OperationRetriesExceededException(count : int, innerException : exn) =
   inherit exn(sprintf "Retry failed after %i attempts." count, innerException)

/// Helper for defining backoffs within the definition of a retry policy for a store.
module Retry =
    /// Wraps an async computation in a retry loop, passing the (1-based) count into the computation and,
    ///   (until `attempts` exhausted) on an exception matching the `filter`, waiting for the timespan chosen by `backoff` before retrying
    let withBackoff (maxAttempts : int) (backoff : int -> System.TimeSpan option) (f : int -> Async<'a>) =
        if maxAttempts < 1 then raise (invalidArg "maxAttempts" "Should be >= 1")
        let rec go attempt = async {
            try
                let! res = f attempt
                return res
            with ex ->
                if attempt = maxAttempts then return raise (OperationRetriesExceededException(maxAttempts, ex))
                else
                    match backoff attempt with
                    | Some timespan -> do! Async.Sleep (int timespan.TotalMilliseconds)
                    | None -> ()
                    return! go (attempt + 1) }
        go 1