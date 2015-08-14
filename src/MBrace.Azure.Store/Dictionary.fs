﻿namespace MBrace.Azure.Store

open System
open System.Runtime.Serialization
open System.IO

open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Table

open MBrace.Core
open MBrace.Store
open MBrace.Store.Internals
open MBrace.Runtime.Vagabond

open MBrace.Azure.Store.TableEntities
open MBrace.Azure.Store.TableEntities.Table
open System.Collections.Generic

/// Azure Table store CloudDictionary implementation.
[<AutoSerializable(true) ; Sealed; DataContract>]
type CloudDictionary<'T> (tableName : string, connectionString) = 
    
    [<DataMember(Name = "ConnectionString")>]
    let connectionString = connectionString
    [<DataMember(Name = "Table")>]
    let tableName = tableName

    [<IgnoreDataMember>]
    let mutable client = Table.getClient(CloudStorageAccount.Parse connectionString)
    [<OnDeserialized>]
    let _onDeserialized (_ : StreamingContext) =
        client <- Table.getClient(CloudStorageAccount.Parse connectionString)
        
    interface ICloudDictionary<'T> with
        member x.IsKnownCount: bool = false
        
        member x.IsKnownSize: bool = false
        
        member this.Add(key: string, value : 'T): Async<unit> = 
            async {
                let binary = VagabondRegistry.Instance.Serializer.Pickle value
                let e = new FatEntity(key, String.Empty, binary)
                do! Table.insert<FatEntity> client tableName e
            }
        
        member this.Transact(key: string, transacter: 'T option -> 'R * 'T, maxRetries: int option): Async<'R> = 
            async {
                let rec transact (e : FatEntity) (count : int) : Async<'R> = async { 
                    match maxRetries with
                    | Some maxRetries when count >= maxRetries -> 
                        return raise <| exn("Maximum number of retries exceeded.")
                    | _ -> ()

                    let value = 
                        match e with
                        | null -> None
                        | e -> Some(VagabondRegistry.Instance.Serializer.UnPickle<'T>(e.GetPayload()))
                        
                    let returnValue, newValue = transacter value
                    let binary = VagabondRegistry.Instance.Serializer.Pickle newValue
                    let e' = new FatEntity(key, String.Empty, binary)
                    match value with
                    | None ->
                        let! result = Async.Catch <| Table.insert<FatEntity> client tableName e'
                        match result with
                        | Choice1Of2 () -> return returnValue
                        | Choice2Of2 ex when Conflict ex ->
                            let! e = Table.read<FatEntity> client tableName key String.Empty
                            return! transact e (count + 1)
                        | Choice2Of2 ex -> return raise ex
                    | Some _ ->
                        e'.ETag <- e.ETag
                        let! result = Async.Catch <| Table.merge<FatEntity> client tableName e'
                        match result with
                        | Choice1Of2 _ -> return returnValue
                        | Choice2Of2 ex when PreconditionFailed ex -> 
                            let! e = Table.read<FatEntity> client tableName key String.Empty
                            return! transact e (count + 1)
                        | Choice2Of2 ex -> return raise ex
                }
                let! e = Table.read<FatEntity> client tableName key String.Empty
                return! transact e 0
            }

        member this.ContainsKey(key: string): Async<bool> = 
            async {
                let! e = Table.read<FatEntity> client tableName key String.Empty
                return e <> null
            }
        
        member this.Count: Local<int64> = Cloud.Raise(new NotSupportedException("Count property not supported."))
        
        member this.Size: Local<int64> = Cloud.Raise(new NotSupportedException("Size property not supported."))

        member this.Dispose(): Local<unit> = 
            async {
                let! _ = client.GetTableReference(tableName).DeleteAsync()
                         |> Async.AwaitTask
                return ()
            } |> Cloud.OfAsync
        
        member this.Id: string = tableName
        
        member this.Remove(key: string): Async<bool> = 
            async {
                try
                    do! Table.delete client tableName (TableEntity(key, String.Empty, ETag = "*"))
                    return true
                with ex -> 
                    if NotFound ex then return false else return raise ex
            }
        
        member this.ToEnumerable(): Local<seq<Collections.Generic.KeyValuePair<string,'T>>> = 
            async {
                let! entities = Table.readAll<FatEntity> client tableName
                return entities
                       |> Seq.map (fun entity -> new KeyValuePair<_,_>(entity.PartitionKey, VagabondRegistry.Instance.Serializer.UnPickle<'T>(entity.GetPayload())))
            } |> Cloud.OfAsync
        
        member this.TryAdd(key: string, value: 'T): Async<bool> = 
            async {
                try
                    do! (this :> ICloudDictionary<'T>).Add(key, value)
                    return true
                with ex ->
                    if Conflict ex then return false else return raise ex
            }
        
        member x.TryFind(key: string): Async<'T option> = 
            async {
                let! e = Table.read<FatEntity> client tableName key String.Empty
                match e with
                | null -> return None
                | e ->
                    let value = VagabondRegistry.Instance.Serializer.UnPickle<'T>(e.GetPayload())
                    return Some value
            }

[<Sealed; DataContract>]
type CloudDictionaryProvider private (connectionString : string) =
    
    [<DataMember(Name = "ConnectionString")>]
    let connectionString = connectionString

    [<IgnoreDataMember>]
    let mutable client = 
        let acc = CloudStorageAccount.Parse(connectionString)
        Table.getClient acc

    [<OnDeserialized>]
    let _onDeserialized (_ : StreamingContext) =
        let acc = CloudStorageAccount.Parse(connectionString)
        client <- Table.getClient acc

    static member Create(connectionString : string) =
        new CloudDictionaryProvider(connectionString)

    interface ICloudDictionaryProvider with
        member x.Create(): Async<ICloudDictionary<'T>> = 
            async {
                let tableName = Table.getRandomName()
                let tableRef = client.GetTableReference(tableName)
                let! _ = tableRef.CreateIfNotExistsAsync()
                return new CloudDictionary<'T>(tableName, connectionString) :> ICloudDictionary<'T>
            }

        member x.IsSupportedValue(value: 'T): bool = 
            VagabondRegistry.Instance.Serializer.ComputeSize value <= int64 TableEntityConfig.MaxPayloadSize
        