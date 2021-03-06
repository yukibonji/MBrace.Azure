﻿namespace MBrace.Azure.Runtime.Utilities

open System
open System.Collections.Generic

open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Table

open MBrace.Core.Internals
open MBrace.Runtime.Utils
open MBrace.Azure.Runtime

[<RequireQualifiedAccess>]
module Table =

    let private random = new Random(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode (obj()))
    let private alpha = [|'a'..'z'|]
    let private alphaNumeric = Array.append alpha [|'0'..'9'|]
    let private randOf (x : char []) = x.[random.Next(0, x.Length)]

    // See http://blogs.msdn.com/b/jmstall/archive/2014/06/12/azure-storage-naming-rules.aspx
    let getRandomName () =
        let alphaNumericLenth = 63 - 1
        let name = 
            [| yield randOf alpha // first character must be alphabet
               for _i = 1 to alphaNumericLenth do yield randOf alphaNumeric |]

        new String(name)

    let getRandomNameWithPrefix (prefix : string) =
        Validate.tableName prefix
        if prefix.Length > 15 then invalidArg "prefix" "must be less than 15 characters"
        let alphaNumericLenth = 63 - prefix.Length
        let suffix = String([| for _i = 1 to alphaNumericLenth do yield randOf alphaNumeric |])
        prefix + suffix

    let private exec<'U> (config : AzureStorageAccount) table op : Async<obj> = 
        async {
            let t = config.TableClient.GetTableReference table
            do! t.CreateIfNotExistsAsyncSafe(maxRetries = 3)
            let! (e : TableResult) = t.ExecuteAsync(op) |> Async.AwaitTaskCorrect
            return e.Result 
        }

    let insert<'T when 'T :> ITableEntity> (config : AzureStorageAccount) table (e : 'T) : Async<unit> = 
        TableOperation.Insert(e) |> exec config table |> Async.Ignore

    let batch (config : AzureStorageAccount) table (operations : TableBatchOperation) = async {
        let jobs = new ResizeArray<Async<unit>>()
        let batch = ref <| new TableBatchOperation()
        let mkHandle batch = Async.StartChild <| async {
            let t = config.TableClient.GetTableReference(table)
            do! t.CreateIfNotExistsAsyncSafe(maxRetries = 3)
            let! _ = t.ExecuteBatchAsync(batch) |> Async.AwaitTaskCorrect
            ()
        }
        for e in operations do
            batch.Value.Add(e)
            if batch.Value.Count = 100 then // Tables support up to 100 ops.
                let! handle = mkHandle batch.Value
                batch := new TableBatchOperation()
                jobs.Add(handle)
        if batch.Value.Count > 0 then
            let! handle = mkHandle batch.Value
            jobs.Add(handle)

        do! Async.Parallel jobs
            |> Async.Ignore
    }

    let insertBatch<'T when 'T :> ITableEntity> config table (es : seq<'T>) : Async<unit> =
        let b = new TableBatchOperation()
        es |> Seq.iter (fun e -> b.Add(TableOperation.Insert(e)))
        batch config table b

    let mergeBatch<'T when 'T :> ITableEntity> config table (es : seq<'T>) : Async<unit> =
        let b = new TableBatchOperation()
        es |> Seq.iter (fun e -> b.Add(TableOperation.Merge(e)))
        batch config table b

    let deleteBatch<'T when 'T :> ITableEntity> config table (es : seq<'T>) : Async<unit> =
        let b = new TableBatchOperation()
        es |> Seq.iter (fun e -> b.Add(TableOperation.Delete(e)))
        batch config table b

    let insertOrReplace<'T when 'T :> ITableEntity> config table (e : 'T) : Async<unit> = 
        TableOperation.InsertOrReplace(e) |> exec config table |> Async.Ignore

    let insertOrMerge<'T when 'T :> ITableEntity> config table (e : 'T) : Async<unit> = 
        TableOperation.InsertOrMerge(e) |> exec config table |> Async.Ignore

    let read<'T when 'T :> ITableEntity> (config : AzureStorageAccount) table pk rk : Async<'T> = async { 
        let t = config.TableClient.GetTableReference(table)
        let! e = t.ExecuteAsync(TableOperation.Retrieve<'T>(pk, rk)) |> Async.AwaitTaskCorrect
        return e.Result :?> 'T
    }

    let queryAsync<'T when 'T : (new : unit -> 'T) and 'T :> ITableEntity> (table : CloudTable) (query : TableQuery<'T>) : Async<ICollection<'T>> = async {
        // taken from http://stackoverflow.com/a/24270388
        let items = new ResizeArray<'T> ()
        let rec runQuery (token : TableContinuationToken) = async {
            let! segment = table.ExecuteQuerySegmentedAsync(query, token) |> Async.AwaitTaskCorrect
            items.AddRange segment
            match segment.ContinuationToken with
            | null -> ()
            | token -> return! runQuery token
        }

        do! runQuery null
        return items :> ICollection<'T>
    }

    let query<'T when 'T : (new : unit -> 'T) and 'T :> ITableEntity> (config : AzureStorageAccount) (table : string) (query : TableQuery<'T>) : Async<ICollection<'T>> = async {
        // taken from http://stackoverflow.com/a/24270388
        let t = config.TableClient.GetTableReference table
        return! queryAsync t query
    }

    let queryPK<'T when 'T : (new : unit -> 'T) and 'T :> ITableEntity> (config : AzureStorageAccount) table pk : Async<ICollection<'T>> = async {  
        let q = TableQuery<'T>().Where(TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, pk))
        return! query config table q
    }

    let queryDynamic (config : AzureStorageAccount) table pk : Async<ICollection<DynamicTableEntity>> = async {  
        let q = TableQuery<DynamicTableEntity>()
                    .Where(TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, pk))
                    .Select([|"RowKey"|])

        return! query config table q
    }

    let readAll<'T when 'T : (new : unit -> 'T) and 'T :> ITableEntity> (config : AzureStorageAccount) table =
        query config table (new TableQuery<'T>())
    
    let merge<'T when 'T :> ITableEntity> config table (e : 'T) : Async<'T> = 
        TableOperation.Merge(e) |> exec config table |> Async.Cast
    
    let tryMerge<'T when 'T :> ITableEntity> config table (e : 'T) : Async<'T option> = async {
        let! result = Async.Catch <| merge<'T> config table e
        match result with
        | Choice1Of2 r -> return Some(r)
        | Choice2Of2 ex when StoreException.PreconditionFailed ex -> return None
        | Choice2Of2 ex -> return raise ex
    }

    let replace<'T when 'T :> ITableEntity> config table (e : 'T) : Async<'T> = 
        TableOperation.Replace(e) |> exec config table |> Async.Cast

    let delete<'T when 'T :> ITableEntity> config table (e : 'T) : Async<unit> =
        TableOperation.Delete(e) |> exec config table |> Async.Ignore

    let transact<'T when 'T :> ITableEntity> config table pk rk (f : 'T -> unit) : Async<'T> = async {
        let rec transact e = async { 
            f e
            let! result = Async.Catch <| merge<'T> config table e
            match result with
            | Choice1Of2 r -> return r
            | Choice2Of2 ex when StoreException.PreconditionFailed ex -> 
                let! e = read<'T> config table pk rk
                return! transact e
            | Choice2Of2 ex -> return raise ex
        }
        let! e = read<'T> config table pk rk
        return! transact e
    }

    let transact2<'T when 'T :> ITableEntity> config table pk rk (f : 'T -> 'T) : Async<'T> = async {
        let rec transact e = async { 
            let e' = f e
            let! result = Async.Catch <| merge<'T> config table e'
            match result with
            | Choice1Of2 r -> return r
            | Choice2Of2 ex when StoreException.PreconditionFailed ex -> 
                let! e = read<'T> config table pk rk
                return! transact e
            | Choice2Of2 ex -> return raise ex
        }
        let! e = read<'T> config table pk rk
        return! transact e
    }