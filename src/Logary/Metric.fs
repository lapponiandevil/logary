﻿module Logary.Metric

open System
open NodaTime
open Hopac
open Hopac.Infixes
open Logary
open Logary.Internals
open Logary.Utils.Aether

// inspiration: https://github.com/Feuerlabs/exometer/blob/master/doc/exometer_probe.md

type Metric =
  private { updateCh : Ch<Value * Units>
            tickCh   : Ch<unit>
            outputs  : Stream.Src<Message> }

type MetricConf =
  { tickInterval : Duration
    name         : PointName
    initialise   : PointName -> Job<Metric>
  }
  static member create (tickInterval : Duration) (name : PointName) creator =
    { tickInterval = tickInterval
      name         = name
      initialise   = creator }

  interface Named with
    member x.name = x.name


let validate (conf : MetricConf) =
  if conf.name = PointName.empty then
    failwith "empty metric name given"

  conf

let create (reduce : 'state -> Value * Units -> 'state)
           initial
           (handleTick : 'state -> 'state * Message list)
           : Job<Metric> =

  let self =
    { updateCh = Ch ()
      tickCh   = Ch ()
      outputs  = Stream.Src.create () }

  let publish acc m =
    acc |> Job.bind (fun _ -> Stream.Src.value self.outputs m)

  let emptyResult =
    Job.result ()

  let server = Job.iterateServer (initial, []) <| fun (state : 'state, msgs) ->
    Alt.choose [
      self.tickCh ^=> fun _ ->
        let state', msgs' = handleTick state
        // first publish the previous messages
        msgs |> List.fold publish emptyResult
        // now publish the new ones
        |> Job.bind (fun _ -> msgs' |> List.fold publish emptyResult)
        |> Job.map (fun _ -> state', [])

      self.updateCh ^-> (reduce state >> fun s -> s, msgs)
    ]

  server >>-. self

/// Internally tell the metric time has passed
let tick m = Ch.send m.tickCh ()

/// Update the metric with a value and its unit
let update (value, units) m = m.updateCh *<- (value, units)

/// Pipe the 'other' stream of Value*Unit pairs into this metric
let consume other m = Stream.iterJob (fun x -> m.updateCh *<- x) other |> Job.start

/// Stream the values of this metric
let tap m =
  Stream.Src.tap m.outputs |> Stream.mapFun (Lens.get Message.Lenses.value_ >> function
    | Gauge (v, u) -> v, u
    | Derived (v, u) -> v, u
    | Event e -> Int64 1L, Units.Scalar)

/// Stream the Messages of this metric
let tapMessages m = Stream.Src.tap m.outputs