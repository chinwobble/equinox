﻿module Samples.Infrastructure.Services

open Microsoft.Extensions.DependencyInjection
open System

let serializationSettings = Newtonsoft.Json.Converters.FSharp.Settings.CreateCorrect()
let genCodec<'Union when 'Union :> TypeShape.UnionContract.IUnionContract>() = Equinox.UnionCodec.JsonUtf8.Create<'Union>(serializationSettings)

type StreamResolver(storage) =
    member __.Resolve
        (   codec : Equinox.UnionCodec.IUnionEncoder<'event,byte[]>,
            fold: ('state -> 'event seq -> 'state),
            initial: 'state,
            snapshot: (('event -> bool) * ('state -> 'event))) =
        match storage with
        | Storage.StorageConfig.Memory store ->
            Equinox.MemoryStore.MemResolver(store, fold, initial).Resolve
        | Storage.StorageConfig.Es (gateway, cache, unfolds) ->
            let accessStrategy = if unfolds then Equinox.EventStore.AccessStrategy.RollingSnapshots snapshot |> Some else None
            Equinox.EventStore.GesResolver<'event,'state>(gateway, codec, fold, initial, ?access = accessStrategy, ?caching = cache).Resolve
        | Storage.StorageConfig.Cosmos (gateway, cache, unfolds, databaseId, connectionId) ->
            let store = Equinox.Cosmos.EqxStore(gateway, Equinox.Cosmos.EqxCollections(databaseId, connectionId))
            let accessStrategy = if unfolds then Equinox.Cosmos.AccessStrategy.Snapshot snapshot |> Some else None
            Equinox.Cosmos.EqxResolver<'event,'state>(store, codec, fold, initial, ?access = accessStrategy, ?caching = cache).Resolve

type ServiceBuilder(storageConfig, handlerLog) =
     let resolver = StreamResolver(storageConfig)

     member __.CreateFavoritesService() =
        let codec = genCodec<Domain.Favorites.Events.Event>()
        let fold, initial = Domain.Favorites.Folds.fold, Domain.Favorites.Folds.initial
        let snapshot = Domain.Favorites.Folds.isOrigin,Domain.Favorites.Folds.compact
        Backend.Favorites.Service(handlerLog, resolver.Resolve(codec,fold,initial,snapshot))

     member __.CreateSaveForLaterService() =
        let codec = genCodec<Domain.SavedForLater.Events.Event>()
        let fold, initial = Domain.SavedForLater.Folds.fold, Domain.SavedForLater.Folds.initial
        let snapshot = Domain.SavedForLater.Folds.isOrigin,Domain.SavedForLater.Folds.compact
        Backend.SavedForLater.Service(handlerLog, resolver.Resolve(codec,fold,initial,snapshot), maxSavedItems=50, maxAttempts=3)

     member __.CreateTodosService() =
        let codec = genCodec<TodoBackend.Events.Event>()
        let fold, initial = TodoBackend.Folds.fold, TodoBackend.Folds.initial
        let snapshot = TodoBackend.Folds.isOrigin, TodoBackend.Folds.compact
        TodoBackend.Service(handlerLog, resolver.Resolve(codec,fold,initial,snapshot))

let register (services : IServiceCollection, storageConfig, handlerLog) =
    let regF (factory : IServiceProvider -> 'T) = services.AddSingleton<'T>(fun (sp: IServiceProvider) -> factory sp) |> ignore

    regF <| fun _sp -> ServiceBuilder(storageConfig, handlerLog)

    regF <| fun sp -> sp.GetService<ServiceBuilder>().CreateFavoritesService()
    regF <| fun sp -> sp.GetService<ServiceBuilder>().CreateSaveForLaterService()
    regF <| fun sp -> sp.GetService<ServiceBuilder>().CreateTodosService()