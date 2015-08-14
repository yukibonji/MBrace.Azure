﻿namespace MBrace.Azure.Runtime.Tests

open System.Threading

open NUnit.Framework
open FsUnit

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Runtime
open MBrace.Azure.Runtime
open MBrace.Azure

#nowarn "445"

open MBrace.Core.Tests

module Utils =
    open System

    let selectEnv name =
        (Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.User),
          Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.Process))
        |> function 
           | s, _, _ when not <| String.IsNullOrEmpty(s) -> s
           | _, s, _ when not <| String.IsNullOrEmpty(s) -> s
           | _, _, s when not <| String.IsNullOrEmpty(s) -> s
           | _ -> failwith "Variable not found"

module Choice =

    let shouldEqual (value : 'T) (input : Choice<'T, exn>) = 
        match input with
        | Choice1Of2 v' -> should equal value v'
        | Choice2Of2 e -> should equal value e

    let shouldMatch (pred : 'T -> bool) (input : Choice<'T, exn>) =
        match input with
        | Choice1Of2 t when pred t -> ()
        | Choice1Of2 t -> raise <| new AssertionException(sprintf "value '%A' does not match predicate." t)
        | Choice2Of2 e -> should be instanceOfType<'T> e

    let shouldFailwith<'T, 'Exn when 'Exn :> exn> (input : Choice<'T, exn>) = 
        match input with
        | Choice1Of2 t -> should be instanceOfType<'Exn> t
        | Choice2Of2 e -> should be instanceOfType<'Exn> e


type RuntimeSession(config : MBrace.Azure.Configuration) =

    let mutable state = None

    member __.Start () = 
        let rt = MBraceAzure.GetHandle(config)

        let logTest = 
            { new ILogTester with
                  member x.Clear() = ()
                  member x.GetLogs() =
                    rt.GetAllProcesses()
                    |> Seq.collect rt.GetCloudLogs
                    |> Seq.map (fun l -> l.Message)
                    |> Seq.toArray }

        state <- Some (rt, logTest)

    member __.Stop () =
        state |> Option.iter (fun (r,l) -> (r.KillLocalWorker() ; r.Reset(true, true, true, true, true)))
        state <- None

    member __.Runtime =
        match state with
        | None -> invalidOp "MBrace runtime not initialized."
        | Some (r,_) -> r

    member __.Logger : ILogTester =
        match state with
        | None -> invalidOp "MBrace runtime not initialized."
        | Some (_,l) -> l